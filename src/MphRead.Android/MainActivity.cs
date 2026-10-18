using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using System.Text;

namespace MphRecomp.App;

// The dev builds' launcher (owner, 2026-09-29: launching straight into the game is for the final version, so a public
// build -- MPH_PUBLIC -- boots into GameActivity and reaches this only through RECOMP SETTINGS > developer): "Play game" opens the game's own menus (GameActivity), plus the room viewer, HD trophy viewers,
// animation viewer, music A/B and the early campaign starts. Hold Select + Start in the menus to come back here.
//   am start -n com.mphrecomp.app/com.mphrecomp.app.MainActivity
[Activity(Name = "com.mphrecomp.app.MainActivity", Label = "MPH Recomp", MainLauncher = !BuildFlags.Public,
    Exported = BuildFlags.ExportDevActivities,
    ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape)]
public class MainActivity : Activity
{
    const int PickRomRequest = 1001;

    static readonly string[] Rooms =
    {
        "MP1 SANCTORUS", "MP2 HARVESTER", "MP3 PROVING GROUND", "MP4 HIGHGROUND",
        "MP5 FUEL SLUICE", "MP6 HEADSHOT", "MP7 PROCESSOR CORE", "MP8 FIRE CONTROL",
        "MP9 CRYOCHASM", "MP10 OVERLOAD", "MP11 BREAKTHROUGH", "MP12 SIC TRANSIT",
        "MP13 ACCELERATOR", "MP14 OUTER REACH",
    };

    static readonly string[] CampaignHunters = { "Samus", "Kanden", "Trace", "Sylux", "Noxus", "Spire", "Weavel" };
    int _campaignHunter;
    // first-person arm cannon for Samus: a Prime gun the owner exported to <external files>/guns/<id>/, or MPH's own
    int _campaignGun;

    // campaign starts: each planet's landing room (a fresh story save, nothing written to disk yet)
    static readonly (string label, string room)[] CampaignStarts =
    {
        ("Celestial Archives -- Celestial Gateway", "UNIT2_LAND"),
        ("Alinos -- Alinos Gateway", "UNIT1_LAND"),
        ("Vesper Defense Outpost -- VDO Gateway", "UNIT3_LAND"),
        ("Arcterra -- Arcterra Gateway", "UNIT4_LAND"),
    };

    // each planet's two boss rooms (first half, second half) and the boss its first visit spawns
    static readonly (string label, string room)[] BossFights =
    {
        ("Celestial Archives 1 -- Cretaphid (Biodefense Chamber 01)", "UNIT2_B1"),
        ("Celestial Archives 2 -- Slench (Biodefense Chamber 05)", "UNIT2_B2"),
        ("Alinos 1 -- Slench (Biodefense Chamber 02)", "UNIT1_B1"),
        ("Alinos 2 -- Cretaphid (Biodefense Chamber 06)", "UNIT1_B2"),
        ("Vesper Defense Outpost 1 -- Cretaphid (Biodefense Chamber 03)", "UNIT3_B1"),
        ("Vesper Defense Outpost 2 -- Slench (Biodefense Chamber 08)", "UNIT3_B2"),
        ("Arcterra 1 -- Slench (Biodefense Chamber 04)", "UNIT4_B1"),
        ("Arcterra 2 -- Cretaphid (Biodefense Chamber 07)", "UNIT4_B2"),
    };

    static readonly (string label, string model)[] Characters =
    {
        ("Samus", "Samus_lod0"), ("Kanden", "Kanden_lod0"), ("Trace", "Trace_lod0"),
        ("Sylux", "Sylux_lod0"), ("Noxus", "Nox_lod0"), ("Spire", "Spire_lod0"), ("Weavel", "Weavel_lod0"),
    };

    static readonly (string label, string model)[] Pickups =
    {
        ("Health (big)", "pick_health_A"), ("Double Damage", "pick_dblDamage"),
        ("Volt Driver", "pick_wpn_electro"), ("Battlehammer", "pick_wpn_jackhammer"),
        ("Imperialist", "pick_wpn_snipergun"), ("Judicator", "pick_wpn_shotgun"),
        ("Magmaul", "pick_wpn_mortar"), ("Shock Coil", "pick_wpn_ghostbuster"),
        ("UA ammo", "pick_ammo_green"),
    };

    TextView _log = null!;
    LinearLayout _content = null!;
    Button _selectBtn = null!;
    readonly StringBuilder _sb = new();
    bool _roomsShown;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // a game started from the front end exits through here: go back to the menus instead. A public build never shows
        // this launcher at all (owner 2026-10-04: no launcher or dev screens in the beta), even after Android restarted
        // the app and forgot AppNav
        if (BuildFlags.Public || (AppNav.ReturnToGame && Intent?.GetStringExtra("dev") != "1"))
        {
            AppNav.ReturnToGame = false;
            AppNav.ReturnedFromGame = true;
            StartActivity(new Intent(this, typeof(GameActivity)).AddFlags(ActivityFlags.ReorderToFront));
            Finish();
            return;
        }
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(36, 40, 36, 20);
        // Android 15+ draws apps edge to edge, under the status bar and this title bar (a Galaxy S23 on Android 16 hid
        // "Play game" that way): drop the title bar and pad the menu clear of the system bars
        ActionBar?.Hide();
        root.SetOnApplyWindowInsetsListener(new SystemBarPadding(36, 40, 36, 20));

        var playBtn = new Button(this) { Text = "Play game", TextSize = 22f };
        playBtn.Click += (_, _) => StartActivity(new Intent(this, typeof(GameActivity)));
        root.AddView(playBtn);

        _selectBtn = new Button(this) { Text = "Select Metroid Prime Hunters ROM…" };
        _selectBtn.Click += (_, _) => PickRom();
        root.AddView(_selectBtn);

        var scroll = new ScrollView(this);
        _content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _log = new TextView(this) { TextSize = 12f };
        _log.SetPadding(0, 16, 0, 8);
        _content.AddView(_log);
        scroll.AddView(_content);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        SetContentView(root);
        System.Threading.Tasks.Task.Run(DecideStartState);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (BuildFlags.Public || (AppNav.ReturnToGame && intent?.GetStringExtra("dev") != "1"))
        {
            AppNav.ReturnToGame = false;
            AppNav.ReturnedFromGame = true;
            StartActivity(new Intent(this, typeof(GameActivity)).AddFlags(ActivityFlags.ReorderToFront));
            Finish();
        }
    }

    void DecideStartState()
    {
        string files = FilesDir!.AbsolutePath;
        if (System.IO.File.Exists(System.IO.Path.Combine(files, ".extracted")))
        {
            Log("Assets ready. Pick a room to fly through it.");
            RunOnUiThread(ShowRooms);
            return;
        }
        string devRom = System.IO.Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? files, "rom.nds");
        if (System.IO.File.Exists(devRom)) { Log($"[dev] Found pushed ROM: {devRom}"); Extract(devRom); return; }
        Log("No assets yet.\nSelect your legally-owned MPH ROM to extract it into app-private storage.\nNothing is bundled or downloaded.");
    }

    void ShowRooms()
    {
        if (_roomsShown) return;
        _roomsShown = true;
        _selectBtn.Text = "Re-extract from another ROM…";
        AddSoloArena();
        AddFirstHunt();
        AddHeader("Music");
        AddButton("Music A/B: original, original + fixes, HQ, HQ + tone (L2 / Select / Y next, L1 back)",
            () => StartActivity(new Intent(this, typeof(MusicTestActivity))));
        // Campaign (early): MphRead's own adventure logic (doors, corridors, locks, pickups, enemies, story save),
        // drawn from MphRead's draw list -- starts a fresh game in a planet's landing room.
        AddHeader("Campaign (early -- MphRead game logic)");
        // which hunter plays the campaign (MphRead supports any; tap to cycle)
        AddSelector(d =>
        {
            _campaignHunter = Wrap(_campaignHunter + d, CampaignHunters.Length);
            return $"Play as: {CampaignHunters[_campaignHunter]}";
        });
        // which first-person gun Samus holds: each exported Prime gun found on the device, then MPH's own (tap to cycle;
        // also switchable in game while paused)
        var guns = new List<string>();
        string gunRoot = System.IO.Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath, "guns");
        if (System.IO.Directory.Exists(gunRoot))
            foreach (string d in System.IO.Directory.GetDirectories(gunRoot))
                if (System.IO.File.Exists(System.IO.Path.Combine(d, "gun.bin"))) guns.Add(System.IO.Path.GetFileName(d));
        // folders are named after the HD suit they belong to (MP1VariaSuit, MP2DarkSuit, Phazonsuit, ...)
        string GunLabel(string g)
        {
            if (g == "settings") return "RECOMP SETTINGS suit";
            if (g == "none") return "MPH's own";
            if (g.Equals("Phazonsuit", StringComparison.OrdinalIgnoreCase)) return "Prime 1 Phazon Suit";
            var m = System.Text.RegularExpressions.Regex.Match(g, @"^MP(\d)(.+?)(Suit)?$");
            return m.Success ? $"Prime {m.Groups[1].Value} {m.Groups[2].Value}{(m.Groups[3].Success ? " Suit" : "")}" : $"Prime ({g})";
        }
        guns.Sort((x, y) => string.Compare(GunLabel(x), GunLabel(y), StringComparison.OrdinalIgnoreCase));
        guns.Add("none");
        // first (the default): --es gun settings = Samus wears the suit RECOMP SETTINGS has, like the real game start
        guns.Insert(0, "settings");
        _campaignGun = Math.Min(_campaignGun, guns.Count - 1);
        AddSelector(d =>
        {
            _campaignGun = Wrap(_campaignGun + d, guns.Count);
            return $"First-person gun: {GunLabel(guns[_campaignGun])}";
        });
        foreach (var (label, roomName) in CampaignStarts)
            AddButton(label, () => StartActivity(new Intent(this, typeof(CampaignActivity))
                .PutExtra("room", roomName).PutExtra("hunter", CampaignHunters[_campaignHunter])
                .PutExtra("gun", guns[_campaignGun])));
        // straight into a boss fight on a fresh story with every weapon (--es loadout all; nothing is saved), as the
        // hunter / gun picked above: replays a fight a save has already beaten (perf checks, boss bugs)
        AddHeader("Boss fights (all weapons, nothing saved)");
        foreach (var (label, roomName) in BossFights)
            AddButton(label, () => StartActivity(new Intent(this, typeof(CampaignActivity))
                .PutExtra("room", roomName).PutExtra("hunter", CampaignHunters[_campaignHunter])
                .PutExtra("gun", guns[_campaignGun]).PutExtra("loadout", "all")));
        // the same rooms with the boss beaten for you once its intro is over (--es boss down, CampaignBossSkip.cs): its
        // death movie, the Octolith, then the escape when you go back out the way you came (owner 2026-10-03: to test
        // the escape's sounds)
        AddHeader("Escape tests (boss beaten for you, all weapons, nothing saved)");
        foreach (var (label, roomName) in BossFights)
            AddButton(label, () => StartActivity(new Intent(this, typeof(CampaignActivity))
                .PutExtra("room", roomName).PutExtra("hunter", CampaignHunters[_campaignHunter])
                .PutExtra("gun", guns[_campaignGun]).PutExtra("loadout", "all").PutExtra("boss", "down")));
        // the endings (owner queue #36, docs/ENDINGS.md): Gorea beaten for you the same way (Core GoreaSkip.cs), then the
        // ending movie and, in the menus, the credits and the results page; the campaign's exit opens the menus
        // (AppNav.ReturnToGame), not this launcher. Gorea 2 comes out once you take the Omega Cannon and step into the arena
        AddHeader("Endings (Gorea beaten for you, then the credits and results; nothing saved)");
        foreach (var (label, roomName) in new[] { ("Bad ending -- Gorea 1 (Gorea_b1)", "Gorea_b1"),
            ("Good ending -- Gorea 2 (Gorea_b2): take the Omega Cannon", "Gorea_b2") })
            AddButton(label, () =>
            {
                AppNav.ReturnToGame = true;
                StartActivity(new Intent(this, typeof(CampaignActivity))
                    .PutExtra("room", roomName).PutExtra("hunter", CampaignHunters[_campaignHunter])
                    .PutExtra("gun", guns[_campaignGun]).PutExtra("loadout", "all").PutExtra("boss", "down"));
            });
        AddMultiplayer(() => guns[_campaignGun]);
        AddHdTrophies();
        AddHeader("Rooms");
        foreach (string name in Rooms) AddButton(name, () => LaunchRoom(name));
        // Third-person animation viewer: the hunter walks/turns/jumps/shoots in Sanctorus driven by the
        // game's own biped animation logic (left stick move, right stick turn/aim, A jump, R2 shoot,
        // D-pad up/down = browse every clip, left/right = switch hunter, L1/R1 = orbit camera).
        AddHeader("Hunter animation viewer (third-person)");
        foreach (var (label, _) in Characters)
            AddButton(label, () => StartActivity(new Intent(this, typeof(RenderActivity))
                .PutExtra("animview", label).PutExtra("room", "MP1 SANCTORUS")));
        AddHeader("Characters");
        foreach (var (label, model) in Characters) AddButton(label, () => Launch("model", model));
        AddHeader("Pickups");
        foreach (var (label, model) in Pickups) AddButton(label, () => Launch("model", model));
    }

    // Set by tapping an HD Trophies button below; consumed by the next Rooms tap, which then
    // launches the old-shader-vs-GX-TEV-interpreter side-by-side comparison in that room instead
    // of a plain room load. Null = Rooms taps behave normally.
    string? _pendingHd;
    TextView _hdStatus = null!;

    // Multiplayer (early): a local match against bots on MphRead's own match logic (CampaignActivity + MatchSession.cs),
    // as the hunter / gun picked above. Arenas cycle through those whose entity data supports the mode.
    int _mpMode, _mpArena, _mpBots = 3, _mpLevel = 1, _mpTime;
    // Multiplayer characters and skins (dev, until the real pickers): your seat's skin and whether the bots wear the
    // troopers too (CampaignActivity's "skins" arg; a trooper skin plays the Trooper, a Dark Samus skin Dark Samus)
    int _mpSkin;
    bool _mpBotSkins;
    static readonly (string Label, string? Id)[] MpSkins =
    {
        ("off (Play as)", null), ("Wave Trooper", "MP1WaveTrooper"), ("Power Trooper", "MP1PowerTrooper"),
        ("Ice Trooper", "MP1IceTrooper"), ("Plasma Trooper", "MP1PlasmaTrooper"),
        ("Dark Samus (Echoes)", "MP2DarkSamus"), ("Dark Samus (Corruption)", "MP3DarkSamus"),
    };
    string? MpSkinsArg()
    {
        var seats = new List<string>();
        if (MpSkins[_mpSkin].Id is string mine) seats.Add("0:" + mine);
        if (_mpBotSkins)
        {
            string[] bots = { "MP1PowerTrooper", "MP1IceTrooper", "MP1PlasmaTrooper" };
            for (int i = 0; i < _mpBots && i < bots.Length; i++) seats.Add($"{i + 1}:{bots[i]}");
        }
        return seats.Count > 0 ? string.Join(",", seats) : null;
    }
    static readonly (string label, float? seconds)[] MatchTimes = { ("mode default", null), ("3 min", 180), ("1 min", 60) };

    void AddMultiplayer(System.Func<string> gun)
    {
        AddHeader("Multiplayer (early -- vs bots, MphRead match logic)");
        var modes = MphRecomp.Multiplayer.MatchSettings.Modes;
        var all = MphRecomp.Multiplayer.MatchArenas.All;
        List<MphRead.RoomMetadata> Arenas()
        {
            var mode = modes[_mpMode];
            try
            {
                // the census reads the extracted ROM's entity files (the same paths CampaignActivity sets up)
                System.IO.Directory.SetCurrentDirectory(FilesDir!.AbsolutePath);
                MphRead.Paths.UpdatePaths();
                MphRead.Paths.ChooseMphPath();
                // Echoes arenas imported in-app (IMPORT GAMES: <external files>/arenas/<Key>, music/), or on the PC and
                // pushed to <external files>/arenas (Core/Arenas/EchoesArena)
                MphRecomp.Arenas.EchoesArena.ImportRoot = GetExternalFilesDir(null)?.AbsolutePath;
                MphRecomp.Arenas.EchoesArena.InstallStaged(System.IO.Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? "", "arenas"));
                // read the list again now the paths are set: imported (Echoes) arenas register then
                var list = MphRecomp.Multiplayer.MatchArenas.All.Where(a => MphRecomp.Multiplayer.MatchArenas.Supports(a, mode, _mpBots + 1)).ToList();
                if (list.Count > 0) return list;
            }
            catch (Exception ex)
            {
                Log("arena census failed: " + ex.Message);
            }
            return all.ToList();
        }
        Button arenaBtn = null!;
        // the arena list depends on the mode and the player count: shown again when either changes
        string ArenaStep(int d)
        {
            var a = Arenas();
            _mpArena = Wrap(_mpArena + d, a.Count);
            return $"Arena: {ArenaLabel(a[_mpArena])}";
        }
        void Refresh() => arenaBtn.Text = ArenaStep(0);
        AddSelector(d =>
        {
            if (d != 0)
            {
                _mpMode = Wrap(_mpMode + d, modes.Count);
                _mpArena = 0;
                Refresh();
            }
            return $"Mode: {modes[_mpMode]}";
        });
        arenaBtn = AddSelector(ArenaStep);
        // 0 bots: a LAN match of just the players (a match on this device alone needs at least one); P8: up to 7 (8 seats)
        AddSelector(d =>
        {
            if (d != 0)
            {
                _mpBots = Wrap(_mpBots + d, 8);
                Refresh();
            }
            return $"Bots: {_mpBots}";
        });
        AddSelector(d => { _mpLevel = Wrap(_mpLevel + d, 3); return $"Bot level: {_mpLevel}"; });
        AddSelector(d => { _mpTime = Wrap(_mpTime + d, MatchTimes.Length); return $"Time limit: {MatchTimes[_mpTime].label}"; });
        AddSelector(d => { _mpSkin = Wrap(_mpSkin + d, MpSkins.Length); return $"Your skin: {MpSkins[_mpSkin].Label}"; });
        var botSkinBtn = new Button(this) { Text = $"Bots as troopers: {(_mpBotSkins ? "on" : "off")}" };
        botSkinBtn.Click += (_, _) => { _mpBotSkins = !_mpBotSkins; botSkinBtn.Text = $"Bots as troopers: {(_mpBotSkins ? "on" : "off")}"; };
        _content.AddView(botSkinBtn);
        Refresh();
        AddButton("Start match", () =>
        {
            if (_mpBots == 0)
            {
                Toast.MakeText(this, "A match on this device needs at least 1 bot (0 bots is for LAN matches)", ToastLength.Long)!.Show();
                return;
            }
            var a = Arenas();
            var intent = new Intent(this, typeof(CampaignActivity))
                .PutExtra("match", modes[_mpMode].ToString())
                .PutExtra("arena", a[_mpArena % a.Count].Name)
                .PutExtra("bots", _mpBots.ToString())
                .PutExtra("botlevel", _mpLevel.ToString())
                .PutExtra("hunter", CampaignHunters[_campaignHunter])
                .PutExtra("gun", gun());
            if (MatchTimes[_mpTime].seconds is float s) intent.PutExtra("time", s.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (MpSkinsArg() is string skins) intent.PutExtra("skins", skins);
            StartActivity(intent);
        });
        // the HEAVY TURRET kill text (owner 2026-10-17, C41): Shooting Gallery, one bot that heads straight for the turret
        // and shoots from it (EchoesTurretBots.ForceSeat). Stand in the turret's entry box as it gets in for the entry kill
        AddButton("Turret kill test (Shooting Gallery, 1 bot mans the turret)", () =>
        {
            Arenas(); // (sets the ROM paths and registers the imported Echoes arenas)
            MphRead.RoomMetadata? gallery = MphRecomp.Multiplayer.MatchArenas.Find("ECHOES SHOOTING GALLERY");
            if (gallery == null)
            {
                Toast.MakeText(this, "Shooting Gallery isn't imported (IMPORT GAMES: Echoes)", ToastLength.Long)!.Show();
                return;
            }
            var intent = new Intent(this, typeof(CampaignActivity))
                .PutExtra("match", MphRead.GameMode.Battle.ToString())
                .PutExtra("arena", gallery.Name)
                .PutExtra("bots", "1")
                .PutExtra("botlevel", "2")
                .PutExtra("hunter", CampaignHunters[_campaignHunter])
                .PutExtra("gun", gun())
                .PutExtra("turrettest", "1");
            StartActivity(intent);
        });
        // the (SENTRY DRONE) kill text (owner 2026-10-17: "We also still need to test drone killing me"): the arena picked above,
        // one Power Trooper bot that sends its drone at you whenever it has you targeted (PlayerEntity.HostDroneTest)
        AddButton("Drone kill test (1 Trooper bot, drone always on)", () =>
        {
            var a = Arenas();
            var intent = new Intent(this, typeof(CampaignActivity))
                .PutExtra("match", MphRead.GameMode.Battle.ToString())
                .PutExtra("arena", a[_mpArena % a.Count].Name)
                .PutExtra("bots", "1")
                .PutExtra("botlevel", "2")
                .PutExtra("hunter", CampaignHunters[_campaignHunter])
                .PutExtra("gun", gun())
                .PutExtra("skins", (MpSkins[_mpSkin].Id is string mine ? "0:" + mine + "," : "") + "1:MP1PowerTrooper")
                .PutExtra("dronetest", "1");
            StartActivity(intent);
        });
        // two devices on one Wi-Fi network (MatchLan.cs): the host's settings above; the joiner plays as its own "Play as"
        AddButton("Host LAN match (another device joins)", () =>
        {
            var a = Arenas();
            var template = MphRecomp.Multiplayer.MatchSettings.Quick(modes[_mpMode], a[_mpArena % a.Count].Name,
                Enum.Parse<MphRead.Hunter>(CampaignHunters[_campaignHunter]), _mpBots, _mpLevel);
            template.TimeLimitSeconds = MatchTimes[_mpTime].seconds;
            MatchLan.Host(this, template, MphRead.Paths.MphKey, CampaignHunters[_campaignHunter], gun());
        });
        AddButton("Join LAN match", () =>
        {
            Arenas(); // sets up the ROM paths, so the ROM revision is known
            MatchLan.Join(this, MphRead.Paths.MphKey, CampaignHunters[_campaignHunter], gun());
        });
        // online through a SecondHunt server's relay (MatchOnline.cs, docs/ONLINE_PLAY.md O3): the host's settings above
        AddButton("ONLINE TEST: HOST (public game on a server)", () =>
        {
            var a = Arenas();
            var template = MphRecomp.Multiplayer.MatchSettings.Quick(modes[_mpMode], a[_mpArena % a.Count].Name,
                Enum.Parse<MphRead.Hunter>(CampaignHunters[_campaignHunter]), _mpBots, _mpLevel);
            template.TimeLimitSeconds = MatchTimes[_mpTime].seconds;
            MatchOnline.Host(this, template, MphRead.Paths.MphKey, CampaignHunters[_campaignHunter], gun());
        });
        AddButton("ONLINE TEST: JOIN (first open game on a server)", () =>
        {
            Arenas(); // the ROM paths, so the ROM revision is known
            MatchOnline.Join(this, MphRead.Paths.MphKey, CampaignHunters[_campaignHunter], gun());
        });
        // online voice chat (O8, VoiceAndroid): the settings the lobby's voice row will have, until it's built
        AddButton("VOICE: mode (push to talk / open mic / off)", () =>
        {
            var v = MphRecomp.Online.Voice.VoiceSettings.Current;
            v.SetMode(v.Mode switch
            {
                MphRecomp.Online.Voice.VoiceMode.PushToTalk => MphRecomp.Online.Voice.VoiceMode.OpenMic,
                MphRecomp.Online.Voice.VoiceMode.OpenMic => MphRecomp.Online.Voice.VoiceMode.Off,
                _ => MphRecomp.Online.Voice.VoiceMode.PushToTalk,
            });
            Toast.MakeText(this, $"Voice: {v.Mode} (push to talk = hold R3 / TALK)", ToastLength.Long)!.Show();
        });
        AddButton("VOICE: with strangers (on / off; friends stay on)", () =>
        {
            var v = MphRecomp.Online.Voice.VoiceSettings.Current;
            v.SetStrangers(!v.Strangers);
            Toast.MakeText(this, $"Voice with strangers: {(v.Strangers ? "ON" : "OFF")}, friends: {(v.Friends ? "ON" : "OFF")}", ToastLength.Long)!.Show();
        });
        // replays (docs/REPLAY_MODE.md R2; App Campaign/MatchReplay.cs): every match above is recorded (RECOMP SETTINGS
        // record replays) and the last 10 kept in <external files>/replays; this plays the newest, checking every step
        AddButton("Play newest replay", () =>
        {
            string data = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
            string? newest = MphRecomp.Replay.ReplayLibrary.Newest(data);
            if (newest == null)
            {
                Toast.MakeText(this, "No replays yet: play a match against bots first", ToastLength.Long)!.Show();
                return;
            }
            Arenas(); // the ROM paths, as for a match
            StartActivity(new Intent(this, typeof(CampaignActivity)).PutExtra("replay", newest).PutExtra("gun", gun()));
        });
    }

    // Solo arena (owner 2026-10-08; App Campaign/SoloArena.cs): any character alone in any multiplayer arena, full control,
    // the third-person camera; R3 switches the right stick between aiming and orbiting the camera round the player.
    // A character with a skin plays through CampaignActivity's "skins" arg (a trooper skin = the Trooper on Weavel, a Dark
    // Samus skin = Dark Samus on Samus; "trooper" = the Trooper in Weavel's own DS model)
    int _soloChar, _soloArena;
    bool _soloFallback;
    bool _soloPlace; // Solo arena: the pickup placement editor (--es place 1)
    bool _soloSelfBody; // Solo arena: the Trooper's drone shots hit his own body (--es selfbody 1, T5)
    int _soloTargets; // Solo arena: bots that stand still and never die (--es bots <n> --es targets 1)
    bool _soloInfUa; // Solo arena: Universal Ammo never runs out (--es infua 1)
    static readonly int[] SoloTargetCounts = { 0, 3, 5, 7 };
    static readonly (string Label, string Hunter, string? Skin)[] SoloCharacters =
    {
        ("Dark Samus (Echoes)", "Samus", "MP2DarkSamus"), ("Overcharged Dark Samus (Echoes)", "Samus", "MP2DarkSamusFinal"),
        ("Dark Samus (Corruption)", "Samus", "MP3DarkSamus"),
        ("Wave Trooper", "Weavel", "MP1WaveTrooper"), ("Power Trooper", "Weavel", "MP1PowerTrooper"),
        ("Ice Trooper", "Weavel", "MP1IceTrooper"), ("Plasma Trooper", "Weavel", "MP1PlasmaTrooper"),
        ("Trooper (Weavel's DS model)", "Weavel", "trooper"),
        ("Zero Suit Samus (Corruption, Weavel's rig + gun)", "Samus", "MP3ZeroSuit"),
        ("Zero Suit Samus (Corruption, Samus's rig, no gun)", "Samus", "MP3ZeroSuitS"),
        ("Zero Suit Samus (Brawl, her own rig)", "Samus", "SSBBZeroSuit"),
        ("Sylux (Prime 4 Beyond: flex / arm swipe flourish)", "Sylux", "MP4Sylux"),
        ("Samus (RECOMP SETTINGS suit)", "Samus", null), ("Kanden", "Kanden", null), ("Spire", "Spire", null),
        ("Trace", "Trace", null), ("Noxus", "Noxus", null), ("Sylux", "Sylux", null), ("Weavel", "Weavel", null),
    };

    void AddSoloArena()
    {
        AddHeader("Solo arena (third person; R3 = orbit camera / aim)");
        List<MphRead.RoomMetadata> Arenas()
        {
            try
            {
                // as the Multiplayer section: the ROM paths, then imported (Echoes) arenas register
                System.IO.Directory.SetCurrentDirectory(FilesDir!.AbsolutePath);
                MphRead.Paths.UpdatePaths();
                MphRead.Paths.ChooseMphPath();
                MphRecomp.Arenas.EchoesArena.ImportRoot = GetExternalFilesDir(null)?.AbsolutePath;
                MphRecomp.Arenas.EchoesArena.InstallStaged(System.IO.Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? "", "arenas"));
                var list = MphRecomp.Multiplayer.MatchArenas.All
                    .Where(a => MphRecomp.Multiplayer.MatchArenas.Supports(a, MphRead.GameMode.Battle, 1)).ToList();
                if (list.Count > 0) return list;
            }
            catch (Exception ex)
            {
                Log("solo arena census failed: " + ex.Message);
            }
            return MphRecomp.Multiplayer.MatchArenas.All.ToList();
        }
        AddSelector(d =>
        {
            _soloChar = Wrap(_soloChar + d, SoloCharacters.Length);
            return $"Character: {SoloCharacters[_soloChar].Label}";
        });
        AddSelector(d =>
        {
            var a = Arenas();
            _soloArena = Wrap(_soloArena + d, a.Count);
            return $"Arena: {ArenaLabel(a[_soloArena])}";
        });
        // Pirate Blaster + Trooper abilities online: the no-import fallback (MatchSkins "lookalikes" arg): a trooper skin
        // drawn as Weavel's DS model in that skin's colours, with the hand-posed wall-cling pose (owner 2026-10-09)
        var fallbackBtn = new Button(this);
        void FallbackText() => fallbackBtn.Text = $"Weavel fallback (look-alikes, no imports): {(_soloFallback ? "ON" : "OFF")}";
        fallbackBtn.Click += (_, _) => { _soloFallback = !_soloFallback; FallbackText(); };
        FallbackText();
        _content.AddView(fallbackBtn);
        // Pickup placement editor (owner 2026-10-10): place / delete pickups (and Echoes affinity crates) in the run, saved
        // to <data>/placements (PickupPlacer.cs)
        var placeBtn = new Button(this);
        void PlaceText() => placeBtn.Text = $"Place pickups (D-pad + Select): {(_soloPlace ? "ON" : "OFF")}";
        placeBtn.Click += (_, _) => { _soloPlace = !_soloPlace; PlaceText(); };
        PlaceText();
        _content.AddView(placeBtn);
        // T5 (Trooper standing body hitbox, owner 2026-10-10): the drone's shots hit his own standing body (SoloArena.cs)
        var selfBodyBtn = new Button(this);
        void SelfBodyText() => selfBodyBtn.Text = $"Trooper: hit your own body (drone out): {(_soloSelfBody ? "ON" : "OFF")}";
        selfBodyBtn.Click += (_, _) => { _soloSelfBody = !_soloSelfBody; SelfBodyText(); };
        SelfBodyText();
        _content.AddView(selfBodyBtn);
        // practice targets + endless UA (owner 2026-10-12: Hyper Ball practice, SoloArena.cs)
        var targetsBtn = new Button(this);
        void TargetsText() => targetsBtn.Text = $"Practice targets (idle bots, endless health): {SoloTargetCounts[_soloTargets]}";
        targetsBtn.Click += (_, _) => { _soloTargets = (_soloTargets + 1) % SoloTargetCounts.Length; TargetsText(); };
        TargetsText();
        _content.AddView(targetsBtn);
        var infUaBtn = new Button(this);
        void InfUaText() => infUaBtn.Text = $"Endless Universal Ammo: {(_soloInfUa ? "ON" : "OFF")}";
        infUaBtn.Click += (_, _) => { _soloInfUa = !_soloInfUa; InfUaText(); };
        InfUaText();
        _content.AddView(infUaBtn);
        AddButton("Start solo", () =>
        {
            var a = Arenas();
            var (_, hunter, skin) = SoloCharacters[_soloChar];
            var intent = new Intent(this, typeof(CampaignActivity))
                .PutExtra("match", "Battle")
                .PutExtra("solo", "1")
                .PutExtra("bots", "0")
                .PutExtra("arena", a[_soloArena % a.Count].Name)
                .PutExtra("hunter", hunter)
                // Samus wears RECOMP SETTINGS' suit; Dark Samus is a skin on Samus's rig, so no suit of Samus's own
                .PutExtra("gun", skin == null && hunter == "Samus" ? "settings" : "none");
            if (skin != null) intent.PutExtra("skins", "0:" + skin);
            if (_soloFallback) intent.PutExtra("lookalikes", "1");
            if (_soloPlace) intent.PutExtra("place", "1");
            if (_soloSelfBody) intent.PutExtra("selfbody", "1");
            if (SoloTargetCounts[_soloTargets] > 0) intent.PutExtra("bots", SoloTargetCounts[_soloTargets].ToString()).PutExtra("targets", "1");
            if (_soloInfUa) intent.PutExtra("infua", "1");
            StartActivity(intent);
        });
        // Brawl Zero Suit Samus on her own rig (owner 2026-10-15): her Brawl idle + run, Samus's strafes / backwards / aim,
        // aiming with her gun arm or Samus's whole upper body; the solo arena picked above, 3 idle bots to shoot at
        foreach ((string label, string aim) in new[] { ("gun arm", "arm"), ("whole upper body", "upper") })
        {
            AddButton($"Brawl Zero Suit (her own rig), aim with the {label}: solo arena above, 3 idle bots", () =>
            {
                var a = Arenas();
                StartActivity(new Intent(this, typeof(CampaignActivity))
                    .PutExtra("match", "Battle")
                    .PutExtra("solo", "1")
                    .PutExtra("bots", "3")
                    .PutExtra("targets", "1")
                    .PutExtra("arena", a[_soloArena % a.Count].Name)
                    .PutExtra("hunter", "Samus")
                    .PutExtra("gun", "none")
                    .PutExtra("skins", "0:SSBBZeroSuit")
                    .PutExtra("zsaim", aim));
            });
        }
        // one tap (owner 2026-10-15): Brawl Zero Suit with her Paralyzer in hand and endless ammo (it spends Universal Ammo);
        // the solo arena and practice targets picked above
        AddButton("Brawl Zero Suit + Paralyzer, endless ammo: solo arena + practice targets above", () =>
        {
            var a = Arenas();
            int bots = SoloTargetCounts[_soloTargets];
            var intent = new Intent(this, typeof(CampaignActivity))
                .PutExtra("match", "Battle")
                .PutExtra("solo", "1")
                .PutExtra("bots", bots.ToString())
                .PutExtra("arena", a[_soloArena % a.Count].Name)
                .PutExtra("hunter", "Samus")
                .PutExtra("gun", "none")
                .PutExtra("skins", "0:SSBBZeroSuit")
                .PutExtra("paralyzer", "1")
                .PutExtra("infua", "1");
            if (bots > 0) intent.PutExtra("targets", "1");
            StartActivity(intent);
        });
        // one tap (owner 2026-10-16): the Character picked above in Shooting Gallery, an idle bot sitting in the turret (back
        // in its seat each time the turret is rebuilt): bomb the slot as each hunter (SoloArena.cs, --es turretbot)
        AddButton("Turret bomb test: Character above, Shooting Gallery, idle bot in the turret", () =>
        {
            var a = Arenas();
            var gallery = a.FirstOrDefault(r => (r.InGameName ?? r.Name).Contains("SHOOTING GALLERY", StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains("SHOOTING GALLERY", StringComparison.OrdinalIgnoreCase));
            if (gallery == null)
            {
                Log("turret test: no Shooting Gallery (import Echoes' multiplayer arenas)");
                return;
            }
            var (_, hunter, skin) = SoloCharacters[_soloChar];
            var intent = new Intent(this, typeof(CampaignActivity))
                .PutExtra("match", "Battle")
                .PutExtra("solo", "1")
                .PutExtra("bots", "1")
                .PutExtra("targets", "1")
                .PutExtra("turretbot", "1")
                .PutExtra("arena", gallery.Name)
                .PutExtra("hunter", hunter)
                .PutExtra("gun", skin == null && hunter == "Samus" ? "settings" : "none");
            if (skin != null) intent.PutExtra("skins", "0:" + skin);
            if (_soloFallback) intent.PutExtra("lookalikes", "1");
            StartActivity(intent);
        });
        // one tap (owner 2026-10-12): Corruption Dark Samus's Hyper Ball against bots that stand still and never die
        AddButton("Hyper Ball practice: Dark Samus (Corruption), Data Shrine, 5 idle bots, endless UA", () =>
        {
            var a = Arenas();
            var shrine = a.FirstOrDefault(r => string.Equals(r.InGameName ?? r.Name, "Data Shrine", StringComparison.OrdinalIgnoreCase)) ?? a[0];
            StartActivity(new Intent(this, typeof(CampaignActivity))
                .PutExtra("match", "Battle")
                .PutExtra("solo", "1")
                .PutExtra("bots", "5")
                .PutExtra("targets", "1")
                .PutExtra("infua", "1")
                .PutExtra("arena", shrine.Name)
                .PutExtra("hunter", "Samus")
                .PutExtra("gun", "none")
                .PutExtra("skins", "0:MP3DarkSamus"));
        });
    }

    // First Hunt, the whole game (docs/FIRST_HUNT_PORT.md, step 2; App FirstHunt/FirstHuntSession.cs): its levels loaded
    // natively from the game imported under <data>/firsthunt/AMFE (MphRecomp.FirstHunt.FirstHuntGame). P0: Samus alone in
    // a level. Import: from <data>/firsthunt.nds (the user's own cart dump, pushed by hand until IMPORT GAMES takes it)
    static readonly (string Room, string Label)[] FirstHuntLevels =
    {
        ("Level SP Regulator", "Regulator (training)"), ("Level SP Morphball", "Morph Ball (training)"),
        ("Level SP Survivor", "Survivor (training)"), ("Level MP1", "Trooper Module"), ("Level MP2", "Assault Cradle"),
        ("Level MP3", "Ancient Vestige"), ("Level MP5", "MP5 (unused)"),
    };
    int _fhLevel;

    void AddFirstHunt()
    {
        AddHeader("First Hunt (whole game, in progress: levels only)");
        string data = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        AddButton("Import First Hunt from firsthunt.nds", () =>
        {
            string nds = System.IO.Path.Combine(data, "firsthunt.nds");
            if (!System.IO.File.Exists(nds)) { Log("First Hunt: no " + nds); return; }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var rom = new MphRecomp.Import.NdsRom(System.IO.File.ReadAllBytes(nds));
                    int n = MphRecomp.FirstHunt.FirstHuntGame.Import(rom, data, line => RunOnUiThread(() => Log(line)));
                    RunOnUiThread(() => Log($"First Hunt imported: {n} files"));
                }
                catch (Exception ex)
                {
                    RunOnUiThread(() => Log("First Hunt import failed: " + ex.Message));
                }
            });
        });
        AddButton("Start First Hunt (title screen, menus)", () =>
        {
            if (!MphRecomp.FirstHunt.FirstHuntGame.Available(data)) { Log("First Hunt isn't imported yet"); return; }
            StartActivity(new Intent(this, typeof(FirstHuntActivity)));
        });
        AddSelector(d =>
        {
            _fhLevel = Wrap(_fhLevel + d, FirstHuntLevels.Length);
            return $"Level: {FirstHuntLevels[_fhLevel].Label}";
        });
        AddButton("Start First Hunt level", () =>
        {
            if (!MphRecomp.FirstHunt.FirstHuntGame.Available(data)) { Log("First Hunt isn't imported yet"); return; }
            StartActivity(new Intent(this, typeof(CampaignActivity))
                .PutExtra("fhlevel", FirstHuntLevels[_fhLevel].Room)
                .PutExtra("gun", "none"));
        });
    }

    // Optional HD trophy models the user dropped into <externalFilesDir>/hd/ (their own Brawl exports).
    // ONE SUBFOLDER PER MODEL: hd/<Model>/ holds that model's .dae + .brres + all its textures, so
    // generically-named textures (e.g. Samus's "Spe", the suits' shared "gun_reflected"/"polySurface521")
    // can't collide across models. The folder NAME is the model id. Tapping one doesn't launch by
    // itself -- it arms a pending comparison; tap a room below to place it there (see LaunchRoom).
    void AddHdTrophies()
    {
        string hdDir = System.IO.Path.Combine(
            GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath, "hd");
        if (!System.IO.Directory.Exists(hdDir)) return;
        // a model = a subfolder that contains at least one .dae
        var models = new System.Collections.Generic.List<string>();
        foreach (string dir in System.IO.Directory.GetDirectories(hdDir))
            if (System.IO.Directory.GetFiles(dir, "*.dae").Length > 0)
                models.Add(System.IO.Path.GetFileName(dir));
        models.Sort();
        if (models.Count == 0) return;
        // Animated: the trophy auto-rigged to its hunter's DS skeleton, driven by the game's own clips in
        // the third-person viewer. The first launch of each trophy fits its rig (a few minutes on-device;
        // cached as <dae>.mphrig next to it afterwards).
        var animatable = models.FindAll(n => MphRecomp.Anim.TrophyRigs.TryHunterFor(n, out _));
        if (animatable.Count > 0)
        {
            AddHeader("HD models animated (tap a game to open it)");
            AddGrouped("anim", animatable, name =>
            {
                MphRecomp.Anim.TrophyRigs.TryHunterFor(name, out MphRead.Hunter rigHunter);
                // the GX program (materials exactly as in Brawl) for every trophy but the three the owner settled on the
                // old hand-tuned shader (2026-09-26): those keep their tuned look
                bool settled = name is "Kanden" or "Sylux" or "Weavel";
                return ($"{name} (on the {rigHunter} rig{(settled ? ", tuned shader" : "")})", () => StartActivity(new Intent(this, typeof(RenderActivity))
                    .PutExtra("animview", rigHunter.ToString()).PutExtra("animhd", name).PutExtra("room", "MP1 SANCTORUS").PutExtra("gxeval", settled ? "0" : "1")));
            });
        }
        AddHeader("HD models: old shader vs GX side by side (tap one, then a room)");
        AddGrouped("compare", models, name => (name, () => ArmHdCompare(name)));
        _hdStatus = new TextView(this) { TextSize = 11f };
        _hdStatus.SetPadding(0, 4, 0, 8);
        _content.AddView(_hdStatus);
        UpdateHdStatus();
    }

    // The HD models sorted by the game they come from ("MP1..." = Metroid Prime, ... "MP4..." = Prime 4: Beyond; the rest are
    // the Brawl trophies), each game a collapsible section -- tap its title to open or close it; which are open is remembered.
    // Inside a game: Samus's suits first, then the hunters, then the Space Pirates (they ride Weavel's rig), by name.
    static readonly (string Prefix, string Title)[] Games =
    {
        ("MP1", "Metroid Prime"), ("MP2", "Metroid Prime 2: Echoes"), ("MP3", "Metroid Prime 3: Corruption"), ("MP4", "Metroid Prime 4: Beyond"),
    };
    static (int Order, string Title) GameOf(string name)
    {
        for (int i = 0; i < Games.Length; i++)
            if (name.Length > 3 && name.StartsWith(Games[i].Prefix, System.StringComparison.Ordinal) && char.IsUpper(name[3])) return (i + 1, Games[i].Title);
        return (0, "Brawl trophies (hunters + Samus)");
    }
    static int KindOf(string name) =>
        !MphRecomp.Anim.TrophyRigs.TryHunterFor(name, out MphRead.Hunter h) ? 3 : h == MphRead.Hunter.Samus ? 0 : name.StartsWith("MP") && h == MphRead.Hunter.Weavel ? 2 : 1;

    void AddGrouped(string section, System.Collections.Generic.List<string> names, System.Func<string, (string Label, System.Action Tap)> item)
    {
        var prefs = GetSharedPreferences("menu", FileCreationMode.Private)!;
        foreach (var g in names.GroupBy(GameOf).OrderBy(g => g.Key.Order))
        {
            string key = section + "/" + g.Key.Title;
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(40, 0, 0, 12);
            foreach (string n in g.OrderBy(KindOf).ThenBy(n => n, System.StringComparer.OrdinalIgnoreCase))
            {
                var (label, tap) = item(n);
                var b = new Button(this) { Text = label };
                b.Click += (_, _) => tap();
                box.AddView(b);
            }
            bool open = prefs.GetBoolean(key, false);
            box.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
            string Title(bool o) => $"{(o ? "▾" : "▸")}  {g.Key.Title}  ({g.Count()})";
            var head = new Button(this) { Text = Title(open), Gravity = GravityFlags.Start | GravityFlags.CenterVertical };
            head.Click += (_, _) =>
            {
                bool o = box.Visibility != ViewStates.Visible;
                box.Visibility = o ? ViewStates.Visible : ViewStates.Gone;
                head.Text = Title(o);
                prefs.Edit()!.PutBoolean(key, o)!.Apply();
            };
            _content.AddView(head);
            _content.AddView(box);
        }
    }

    void ArmHdCompare(string name)
    {
        _pendingHd = name;
        UpdateHdStatus();
    }

    void UpdateHdStatus() => _hdStatus.Text = _pendingHd != null
        ? $"Comparing '{_pendingHd}' (old shader vs GX-TEV interpreter, side by side) -- tap a room below."
        : "Tap a trophy above, then a room below, to compare old shader vs GX-TEV interpreter side by side in it.";

    void LaunchRoom(string roomName)
    {
        if (_pendingHd != null)
        {
            string model = _pendingHd;
            _pendingHd = null;
            UpdateHdStatus();
            StartActivity(new Intent(this, typeof(RenderActivity))
                .PutExtra("hd", model).PutExtra("hdroom", roomName).PutExtra("gxcompare", "1"));
            return;
        }
        Launch("room", roomName);
    }

    void AddHeader(string text)
    {
        var h = new TextView(this) { Text = text, TextSize = 16f };
        h.SetPadding(0, 24, 0, 8);
        _content.AddView(h);
    }

    // a selector: < [its value] > (owner 2026-10-10: arrows on every dev-menu selector). step(d) moves the choice by d
    // (-1, +1; 0 = as it is) and returns the text to show; a tap on the value itself steps forward, as before
    Button AddSelector(System.Func<int, string> step)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var prev = new Button(this) { Text = "◀" };
        var value = new Button(this) { Text = step(0) };
        var next = new Button(this) { Text = "▶" };
        prev.Click += (_, _) => value.Text = step(-1);
        value.Click += (_, _) => value.Text = step(1);
        next.Click += (_, _) => value.Text = step(1);
        row.AddView(prev, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        row.AddView(value, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(next, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        _content.AddView(row);
        return value;
    }

    // i wrapped into 0..n-1 (both ways)
    static int Wrap(int i, int n) => n <= 0 ? 0 : ((i % n) + n) % n;

    // an arena selector's text: the in-game name, plus "(Expanded)" on the ROM's larger versions of an arena (Transfer
    // Lock's BT room, Fault Line, Elder Passage), so two rooms with one in-game name tell apart (owner 2026-10-10)
    static string ArenaLabel(MphRead.RoomMetadata r)
    {
        string name = r.InGameName ?? r.Name;
        bool expanded = r.Name.Contains("EXPANDED") || r.Name == "AD1 TRANSFER LOCK BT";
        return expanded ? name + " (Expanded)" : name;
    }

    void AddButton(string label, System.Action onClick)
    {
        var b = new Button(this) { Text = label };
        b.Click += (_, _) => onClick();
        _content.AddView(b);
    }

    void Launch(string key, string value) =>
        StartActivity(new Intent(this, typeof(RenderActivity)).PutExtra(key, value));

    void PickRom()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        StartActivityForResult(Intent.CreateChooser(intent, "Select ROM"), PickRomRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickRomRequest || resultCode != Result.Ok || data?.Data == null) return;
        var uri = data.Data;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string dest = System.IO.Path.Combine(FilesDir!.AbsolutePath, "rom.nds");
                Log("Copying ROM into app storage…");
                using (var input = ContentResolver!.OpenInputStream(uri!)!)
                using (var output = System.IO.File.Create(dest))
                    input.CopyTo(output);
                Extract(dest);
            }
            catch (System.Exception ex) { Log("ERROR copying ROM: " + ex.Message); }
        });
    }

    void Extract(string romPath)
    {
        try
        {
            string files = FilesDir!.AbsolutePath;
            System.IO.Directory.SetCurrentDirectory(files);
            var prev = System.Console.Out;
            System.Console.SetOut(new ForwardWriter(Log));
            try { Log("Extracting…"); MphRead.Extract.Setup(romPath); }
            finally { System.Console.SetOut(prev); }

            string tree = System.IO.Path.Combine(files, "files");
            int count = System.IO.Directory.Exists(tree)
                ? System.IO.Directory.GetFiles(tree, "*", System.IO.SearchOption.AllDirectories).Length : 0;
            if (count == 0) { Log("ERROR: no files extracted (unrecognized ROM?)."); return; }
            string root = "unknown";
            var dirs = System.IO.Directory.GetDirectories(tree);
            if (dirs.Length > 0) root = System.IO.Path.GetFileName(dirs[0]);
            System.IO.File.WriteAllText(System.IO.Path.Combine(files, ".extracted"),
                $"version={MphRead.AppInfo.Version}\nrom={root}\nfiles={count}");
            Log($"\n✅ Extraction complete. {root}, {count} files.");
            RunOnUiThread(ShowRooms);
        }
        catch (System.Exception ex) { Log("EXTRACTION FAILED: " + ex); }
    }

    void Log(string msg)
    {
        lock (_sb) _sb.AppendLine(msg);
        Android.Util.Log.Info("MPHExtract", msg);
        RunOnUiThread(() => { lock (_sb) _log.Text = _sb.ToString(); });
    }

    sealed class ForwardWriter : System.IO.TextWriter
    {
        readonly System.Action<string> _sink;
        readonly StringBuilder _line = new();
        public ForwardWriter(System.Action<string> sink) => _sink = sink;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char c) { if (c == '\n') { _sink(_line.ToString()); _line.Clear(); } else if (c != '\r') _line.Append(c); }
        public override void Write(string? s) { if (!string.IsNullOrEmpty(s)) foreach (char c in s!) Write(c); }
    }
}

// Pads a view by the system bars (and display cutout) on top of its own padding, for edge-to-edge windows.
sealed class SystemBarPadding : Java.Lang.Object, View.IOnApplyWindowInsetsListener
{
    readonly int _left, _top, _right, _bottom;

    public SystemBarPadding(int left, int top, int right, int bottom)
    {
        _left = left; _top = top; _right = right; _bottom = bottom;
    }

    public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
    {
        int l, t, r, b;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            Android.Graphics.Insets bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            (l, t, r, b) = (bars.Left, bars.Top, bars.Right, bars.Bottom);
        }
        else
        {
#pragma warning disable CA1422 // the pre-API-30 way, only reached below API 30
            (l, t, r, b) = (insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop,
                insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CA1422
        }
        v.SetPadding(_left + l, _top + t, _right + r, _bottom + b);
        return insets;
    }
}
