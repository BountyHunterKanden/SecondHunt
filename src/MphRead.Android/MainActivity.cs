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
        AddHeader("Music");
        AddButton("Music A/B: original, original + fixes, HQ, HQ + tone (L2 / Select / Y next, L1 back)",
            () => StartActivity(new Intent(this, typeof(MusicTestActivity))));
        // Campaign (early): MphRead's own adventure logic (doors, corridors, locks, pickups, enemies, story save),
        // drawn from MphRead's draw list -- starts a fresh game in a planet's landing room.
        AddHeader("Campaign (early -- MphRead game logic)");
        // which hunter plays the campaign (MphRead supports any; tap to cycle)
        var hunterBtn = new Button(this) { Text = $"Play as: {CampaignHunters[_campaignHunter]}" };
        hunterBtn.Click += (_, _) =>
        {
            _campaignHunter = (_campaignHunter + 1) % CampaignHunters.Length;
            hunterBtn.Text = $"Play as: {CampaignHunters[_campaignHunter]}";
        };
        _content.AddView(hunterBtn);
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
        var gunBtn = new Button(this) { Text = $"First-person gun: {GunLabel(guns[_campaignGun])}" };
        gunBtn.Click += (_, _) =>
        {
            _campaignGun = (_campaignGun + 1) % guns.Count;
            gunBtn.Text = $"First-person gun: {GunLabel(guns[_campaignGun])}";
        };
        _content.AddView(gunBtn);
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
                // Echoes arenas imported on the PC and pushed to <external files>/arenas (Core/Arenas/EchoesArena)
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
        string ArenaName() { var a = Arenas(); return a[_mpArena % a.Count].InGameName ?? a[_mpArena % a.Count].Name; }
        Button modeBtn = null!, arenaBtn = null!;
        void Refresh()
        {
            modeBtn.Text = $"Mode: {modes[_mpMode]}";
            arenaBtn.Text = $"Arena: {ArenaName()}";
        }
        modeBtn = new Button(this);
        modeBtn.Click += (_, _) => { _mpMode = (_mpMode + 1) % modes.Count; _mpArena = 0; Refresh(); };
        _content.AddView(modeBtn);
        arenaBtn = new Button(this);
        arenaBtn.Click += (_, _) => { _mpArena++; Refresh(); };
        _content.AddView(arenaBtn);
        var botsBtn = new Button(this) { Text = $"Bots: {_mpBots}" };
        // 0 bots: a LAN match of just the players (a match on this device alone needs at least one)
        botsBtn.Click += (_, _) => { _mpBots = (_mpBots + 1) % 4; botsBtn.Text = $"Bots: {_mpBots}"; Refresh(); };
        _content.AddView(botsBtn);
        var levelBtn = new Button(this) { Text = $"Bot level: {_mpLevel}" };
        levelBtn.Click += (_, _) => { _mpLevel = (_mpLevel + 1) % 3; levelBtn.Text = $"Bot level: {_mpLevel}"; };
        _content.AddView(levelBtn);
        var timeBtn = new Button(this) { Text = $"Time limit: {MatchTimes[_mpTime].label}" };
        timeBtn.Click += (_, _) => { _mpTime = (_mpTime + 1) % MatchTimes.Length; timeBtn.Text = $"Time limit: {MatchTimes[_mpTime].label}"; };
        _content.AddView(timeBtn);
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
