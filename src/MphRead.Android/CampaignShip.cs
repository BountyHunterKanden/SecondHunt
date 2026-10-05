using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Opengl;
using Android.Util;
using Android.Views;
using Android.Widget;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Media;

namespace MphRecomp.App;

// The gunship between planets: the adventure's progression loop around CampaignHost. Boarding the ship (the hatch's
// YES) ends the hosted session; the ship screen then offers the save, the star map of unlocked planets (MphRead's own
// unlock rules, see ShipNavigation) and launches a new session in the chosen planet's landing room carrying the
// story save. Also: continuing a save file (--es save <slot>), the ending (save marked cleared), and quitting at game
// over (load the last save / new game / quit); --es slot <n> picks the file a new game saves to. Saves: <external files>/saves/campaign<slot>.json (CampaignSaves).
// Boarding opens the game's own in-ship menu (Core Frontend/ShipMenu, from the ROM's shipGrnd graphics): SAVE GAME
// saves here with no movie, EXIT SHIP returns on foot to the landing room (no landing movie), LAUNCH SHIP rises off
// the pad in the cockpit view, fades out and plays the planet's take-off movie, then the planet select (Core
// Frontend/ShipPlanetSelect, the game's own ShipInSpace screen: pick a planet on the star map, LAND SHIP). A first
// visit lands in the game (the landing room plays its landing movie and camera sequence); a revisit plays the landing
// movie, then the cockpit view coming down onto the pad with the in-ship menu. The ending's save prompt and game over
// are still a plain gamepad-driven text panel.
internal sealed partial class CampaignRenderer
{
    // Veil: the screen held white while the next view loads (VeilThen)
    enum ShipPage { Closed, Menu, Takeoff, Briefing, SavePrompt, PlanetSelect, GameOver, Veil }

    ShipBriefing? _briefing;
    ShipPlanetSelect? _planetSelect;
    // the view out of the window at the planet select: the deep-space model in a scene of its own (DeepSpaceView)
    DeepSpaceView? _deepSpace;
    // what Render() draws: the campaign session, or else the planet select's deep space
    Scene? ViewScene => _host?.Scene ?? _deepSpace?.Scene;
    // one texture cache for every ship screen: the drawer keeps GL textures by cache id
    readonly UiTextureCache _shipTextures = new();

    ShipMenu? _shipMenu;
    readonly UiGlDrawer _shipDrawer = new();
    MoviePlayer? _takeoff;
    bool _shipMenuAtStart, _shipNotStarted;
    CameraSequence? _cockpitSeq;
    // a revisit (LAND SHIP on a planet whose landing room was visited): the cockpit view starts with the planet's LAND
    // camseq, then loops (mode 12 init, u16 0x020E5514 == 0 after the planet select's LAND)
    bool _cockpitLanding;
    // LAUNCH SHIP: 60 Hz ticks since the take-off camseq started (-1: not launching)
    int _launchTick = -1;
    // boarding through the hatch: the game asks "do you want to SAVE GAME?" by itself (flag 0x1000, cleared by LAND)
    bool _shipAskSave;
    PlatformEntity? _shipEntity;
    double _cockpitAccum;
    MoviePresenter? _takeoffPresenter;
    readonly System.Collections.Concurrent.ConcurrentQueue<(float X, float Y, int Kind)> _shipTouches = new();
    bool _loadoutChanged;
    readonly System.Diagnostics.Stopwatch _shipClock = new();
    double _shipLast;
    // the ship screens' 60 Hz ticks (their timings are vanilla vblanks): the GL thread runs at the display's rate, 120 Hz
    // on the Odin, where one tick per frame ran the planet select, briefing and menu at double speed (owner queue #25)
    double _shipTickAccum;

    // Vanilla's white veil around the cockpit view (revisit_spec.md, rev 1): a revisit's landing movie fades in from
    // white (~4 vblanks) and out to white (10) after its last frame, mode 12 loads behind a held white screen, then fades
    // in from white over 5 vblanks (20 after boarding through the hatch). Ours draws the cockpit room's first frames
    // (texture uploads) behind the held white too: before, the menu came up over an empty, black window for that moment
    // (owner queue #10). A revisit's room loads on a worker during the landing movie (PreloadCockpitView: ~1.3 s on the
    // Odin, which held the white that much longer than vanilla's 25 vblanks); the white then holds vanilla's 25.
    const double VeilMovieIn = 4 / 60.0, VeilMovieOut = 10 / 60.0, VeilLandIn = 5 / 60.0, VeilHatchIn = 20 / 60.0;
    // mode 12's load after a revisit's landing movie: the screen held white for vblanks 301-326 (revisit_spec.md)
    const double VeilLandHold = 25 / 60.0;
    // frames kept fully white after the load: the first draws of the new room (uploads) happen under it
    const int VeilHoldFrames = 3;
    float _veil;
    bool _veilDrawn;
    Action? _veilThen;
    int _veilHold;
    double _veilFade, _veilFadeLeft, _veilMinHold, _veilWhiteAt;
    // a revisit's cockpit room, loading on a worker while the landing movie plays; StartCockpitView adopts it
    System.Threading.Tasks.Task<CampaignHost>? _cockpitPreload;
    // the ship movie playing fades from / to white, and what's left of its fade-out after the last frame (-1: not yet)
    bool _movieWhite;
    double _movieOutLeft = -1;
    UiDrawList? _veilList;

    // centred ship-screen text ("" hides it) and the way back to the launcher; set by AttachShip
    public Action<string>? ShipSink;
    public Action? ExitSink;

    CampaignSaves? _saves;
    int _saveSlot = 1;
    int? _continueSlot;

    ShipPage _shipPage;
    StorySave? _shipStory;
    Planet _shipPlanet = Planet.CelestialArchives;
    bool _shipCleared, _shipEnding, _shipUnsaved;
    // the planets the planet select has shown (CampaignSaveFile.KnownAreas)
    int _knownAreas;
    int _shipCursor;
    string _shipNote = "";
    long _playFrames;
    bool _sPrevA, _sPrevB, _sPrevUp, _sPrevDown, _sPrevLeft, _sPrevRight;

    bool ShipOpen => _shipPage != ShipPage.Closed;
    long PlaySeconds => _playFrames / 60;

    // Adds the ship panel to the activity's layout and reads its intent extras: --es save <slot> starts at the star
    // map with that slot's save (and saves back to it); without it saves go to slot 1.
    public static void AttachShip(Activity activity, FrameLayout root, CampaignRenderer renderer, Intent? intent)
    {
        string baseDir = activity.GetExternalFilesDir(null)?.AbsolutePath ?? activity.FilesDir!.AbsolutePath;
        renderer._saves = new CampaignSaves(System.IO.Path.Combine(baseDir, "saves"));
        // --es slot <n>: the file a NEW game saves to (the front end's files A/B/C = slots 1/2/3)
        if (Int32.TryParse(intent?.GetStringExtra("slot"), out int newSlot) && newSlot > 0)
        {
            renderer._saveSlot = newSlot;
        }
        if (Int32.TryParse(intent?.GetStringExtra("save"), out int slot) && slot > 0)
        {
            renderer._continueSlot = slot;
            renderer._saveSlot = slot;
        }
        // --es shipmenu 1: start in the in-ship menu at Celestial Archives (testing; with --es save <n>, that file)
        renderer._shipMenuAtStart = intent?.GetStringExtra("shipmenu") == "1";
        var panel = new TextView(activity) { TextSize = 17f, Visibility = ViewStates.Gone };
        panel.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        panel.SetTextColor(Color.Rgb(200, 255, 220));
        panel.SetBackgroundColor(Color.Argb(215, 4, 18, 24));
        panel.SetPadding(48, 36, 48, 36);
        root.AddView(panel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center });
        if (renderer.PauseMenu != null)
        {
            renderer.PauseMenu.PlaySound = renderer.PlayUiSound; // the pause menu's clicks
        }
        renderer.ShipSink = text => activity.RunOnUiThread(() =>
        {
            panel.Text = text;
            panel.Visibility = text.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
        });
        renderer.ExitSink = () => activity.RunOnUiThread(() =>
        {
            activity.StartActivity(new Intent(activity, typeof(MainActivity)).AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop));
            activity.Finish();
        });
    }

    // first surface: continuing a save opens the planet select instead of starting a room
    bool TryStartAtShip()
    {
        if (_shipMenuAtStart)
        {
            _shipMenuAtStart = false;
            if (_continueSlot is int s && _saves != null && _saves.TryLoad(s, out CampaignSaveFile? f, out _))
            {
                AdoptSave(f!);
            }
            _shipStory ??= new StorySave();
            CampaignSaves.RepairLogbook(_shipStory);
            VeilThen(() => OpenShip(ShipPage.Menu), VeilLandIn);
            return true;
        }
        if (_continueSlot is not int slot || _saves == null)
        {
            return false;
        }
        if (_saves.TryLoad(slot, out CampaignSaveFile? file, out string? error))
        {
            AdoptSave(file!);
            _shipNote = "";
            if (!file!.Started)
            {
                StartNewGameIntro();
            }
            else
            {
                OpenShip(ShipPage.PlanetSelect);
            }
        }
        else
        {
            _shipNote = error == null ? $"Slot {slot} is empty." : $"Slot {slot} could not be read: {error}";
            OpenShip(ShipPage.GameOver);
        }
        return true;
    }

    void AdoptSave(CampaignSaveFile file)
    {
        _hunter = file.Hunter;
        _shipStory = file.Story;
        _shipPlanet = file.Planet;
        _shipCleared = file.Cleared;
        _shipNotStarted = !file.Started;
        _knownAreas = file.KnownAreas ?? file.Story.Areas;
        MapAdoptConnectors(file.MapConnectors);
        _playFrames = file.PlaySeconds * 60;
        _shipUnsaved = false;
    }

    // CampaignHost.Ended: work out why, keep the story, drop the scene and open the ship screen
    void OnHostEnded()
    {
        CampaignHost host = _host!;
        CampaignEnd end = ShipNavigation.Classify(host);
        int roomId = host.RoomId;
        _playFrames += host.Frame;
        StorySave story = CampaignSaves.Clone(MphRead.GameState.StorySave);
        CampaignSaves.RepairLogbook(story);
        Log.Info("MPHCampaign", $"session ended in room {roomId}: {end}");
        try
        {
            host.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", "dispose: " + ex.Message);
        }
        _host = null;
        ReleaseSceneGpu();
        if (end == CampaignEnd.EnteredShip || end == CampaignEnd.Ending)
        {
            ShipNavigation.PrepareForSave(story);
            _shipStory = story;
            _shipUnsaved = true;
            _shipEnding = end == CampaignEnd.Ending;
            if (_shipEnding)
            {
                _shipCleared = true;
                _shipPlanet = Planet.Oubliette;
            }
            else
            {
                _shipPlanet = ShipNavigation.ByLandingRoom(roomId)?.Id ?? _shipPlanet;
            }
            _shipNote = "";
            // the ending keeps the stand-in save prompt; boarding at a landing site opens the game's in-ship menu
            _shipAskSave = !_shipEnding;
            if (_shipEnding) OpenShip(ShipPage.SavePrompt);
            else VeilThen(() => OpenShip(ShipPage.Menu), VeilHatchIn); // the game faded out to white already
        }
        else
        {
            _shipNote = "";
            OpenShip(ShipPage.GameOver);
        }
    }

    // ---------------------------------------------------------------- the in-ship menu (ROM graphics)

    // menu clicks through the campaign's own ROM sound player (CampaignAudio.cs); GL thread
    internal void PlayUiSound(int sfxId) => _sfx?.PlayUi(sfxId);
    // the screen's DS script clock (AndroidSfxPlayer.PlayUi): the in-ship menu steps scripts at 60 Hz, the briefing and
    // the planet select at 30 Hz, an id played with bit 31 at half (the briefing's TELEPATHIC_MESSAGE)
    void PlayShipMenuSound(int sfxId) => _sfx?.PlayUi(sfxId, scriptRate: 60);
    void PlayBriefingSound(int sfxId) => _sfx?.PlayUi(sfxId, scriptRate: sfxId == ShipBriefing.Telepathic ? 15 : 30);
    // the game's StopFreeSfxScripts(1), which the ship screens call before each of their scripts: the UI's scripts only
    void StopUiScripts() => _sfx?.StopUiScripts();
    // a looping UI sample (OPTIONS_SENSITIVITY_LOOP, the logbook's scroll loops, SHIP_THRUST_LOOP) and its stop -- the
    // DS's StopSfxHandle, a ~55 ms release
    int PlayUiLoop(int sfxId) => _sfx?.PlaySample(sfxId, source: null, loop: null, noUpdate: false, recency: -1,
        sourceOnly: false, cancellable: false) ?? -1;
    void StopUiLoop(int handle) => _sfx?.StopSoundByHandle(handle);
    bool UiLoopAlive(int handle) => _sfx?.IsHandlePlaying(handle) ?? false;

    // taps on the activity's touch screen, in surface pixels (UI thread -> GL thread)
    // kind: 0 down, 1 move, 2 up
    public void ShipTouch(float x, float y, int kind = 0) => _shipTouches.Enqueue((x, y, kind));
    public bool ShipMenuOpen => _shipPage is ShipPage.Menu or ShipPage.Briefing or ShipPage.PlanetSelect;

    void OpenShipMenu()
    {
        if (_shipMenu == null)
        {
            _shipMenu = new ShipMenu(Paths.FileSystem, _shipTextures);
            _shipMenu.Log = m => Log.Info("MPHCampaign", m);
            _shipMenu.SaveGame = SaveShip;
            _shipMenu.PlaySound = PlayShipMenuSound;
            _shipMenu.StopScripts = StopUiScripts;
            _shipMenu.PlayLoop = PlayUiLoop;
            _shipMenu.StopLoop = StopUiLoop;
            _shipMenu.SetLoopPitch = (h, pitch) => _sfx?.UpdateUiHandle(h, pitch: pitch);
            _shipMenu.Request = OnShipRequest;
            _shipMenu.WeaponsChanged = () => _loadoutChanged = true;
            _shipMenu.OptionsChanged = () =>
            {
                // the game's OPTIONS page: kept with the recomp settings (the campaign reads them for aiming)
                var settings = MphRecomp.Config.RecompSettings.Load(_gunSettingsPath);
                settings.GameControlType = _shipMenu.ControlType;
                settings.GameSensitivity = _shipMenu.Sensitivity;
                settings.GameLookInvert = _shipMenu.LookInvert;
                settings.Save(_gunSettingsPath);
            };
        }
        var saved = MphRecomp.Config.RecompSettings.Load(_gunSettingsPath);
        _shipMenu.ControlType = saved.GameControlType;
        _shipMenu.Sensitivity = saved.GameSensitivity;
        _shipMenu.LookInvert = saved.GameLookInvert;
        _shipMenu.RedrawArt = saved.RedrawArt;
        _loadoutChanged = false;
        while (_shipTouches.TryDequeue(out _)) { }
        _shipMenu.Story = _shipStory;
        // mode 12 (the ship on the ground) selects the ship's echo at init (rev 1 0x2111558)
        DsEcho.SetPreset(0);
        _shipMenu.Open(ShipNavigation.Get(_shipPlanet).Name, askSave: _shipAskSave);
        _shipAskSave = false;
        _launchTick = -1;
        StartCockpitView();
    }

    static (int Loop, int Land, int Takeoff) CockpitSeqs(Planet planet) => ShipNavigation.CockpitSeqs(planet);

    void StartCockpitSeq(int id, bool loop)
    {
        _cockpitSeq = CameraSequence.Load(id, _host!.Scene);
        _cockpitSeq.Initialize();
        _cockpitSeq.SetUp(_host.Player.CameraInfo, 0);
        _cockpitSeq.Flags |= CamSeqFlags.BlockInput | (loop ? CamSeqFlags.Loop : CamSeqFlags.None);
        _host.Player.CameraInfo.Update();
    }

    // The view out of the window: the landing room runs (with no input) under the cockpit's own camera sequences --
    // unitN_land_cockpit: parked on the pad with a slight bob (the loop), coming down onto the pad (a revisit's
    // landing, then the loop), rising off it (LAUNCH SHIP) -- with the gunship (whose nose the camera sits in) and the
    // player's body hidden. EXIT SHIP ends the sequence and hands this same session to the player; LAUNCH SHIP drops it
    // after the take-off.
    void StartCockpitView()
    {
        var preload = _cockpitPreload;
        _cockpitPreload = null;
        if (_host != null || _shipStory == null) return;
        PlanetInfo planet = ShipNavigation.Get(_shipPlanet);
        bool landing = _cockpitLanding;
        _cockpitLanding = false;
        try
        {
            _host = TakeCockpitPreload(preload) ?? CampaignHost.Start(planet.LandingRoom, save: CampaignSaves.Clone(_shipStory),
                hunter: _hunter, collectDrawItems: true, viewWidth: _width, viewHeight: _height, arriving: false, holdSpawn: true);
            var seqs = CockpitSeqs(_shipPlanet);
            int id = landing ? seqs.Land : seqs.Loop;
            StartCockpitSeq(id, loop: !landing);
            // the cockpit plays its own music, not the room's (cancels the room track the load queued)
            MphRead.Music.PlaySeq(ShipNavigation.CockpitMusic(_shipPlanet));
            foreach (PlatformEntity platform in _host.Scene.GetPlatformEntities())
            {
                if (platform.Flags.TestFlag(PlatformFlags.SamusShip))
                {
                    platform.Hidden = true;
                    _shipEntity = platform;
                }
            }
            PlayerEntity.HostHideMainPlayer = true;
            _cockpitAccum = 0;
            Log.Info("MPHCampaign", $"cockpit view: {planet.LandingRoom}, camseq {id}, ship {(_shipEntity == null ? "not found" : "hidden")}");
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", "cockpit view unavailable: " + ex);
            DropCockpitView();
        }
    }

    // A revisit's landing: the cockpit room loads on a worker while the landing movie plays. Nothing else runs MphRead
    // meanwhile -- the deep space view is gone, Render has no scene, and ShipInput leaves Music alone until
    // StartCockpitView adopts the room (its load queues the room's own track) -- and MphRead's door transitions load
    // rooms on a worker the same way.
    void PreloadCockpitView(PlanetInfo planet)
    {
        if (_host != null || _shipStory == null) return;
        StorySave save = CampaignSaves.Clone(_shipStory);
        Hunter hunter = _hunter;
        int width = _width, height = _height;
        string room = planet.LandingRoom;
        _cockpitPreload = System.Threading.Tasks.Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CampaignHost host = CampaignHost.Start(room, save: save, hunter: hunter, collectDrawItems: true,
                viewWidth: width, viewHeight: height, arriving: false, holdSpawn: true);
            Log.Info("MPHCampaign", $"cockpit view: {room} loaded on a worker in {sw.ElapsedMilliseconds} ms");
            return host;
        });
    }

    // the worker's room, waited for if the movie ended (or was skipped) first; null (load it here) if it failed
    static CampaignHost? TakeCockpitPreload(System.Threading.Tasks.Task<CampaignHost>? preload)
    {
        if (preload == null) return null;
        try
        {
            if (!preload.IsCompleted) Log.Info("MPHCampaign", "cockpit view: waiting for the worker's load");
            return preload.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", "cockpit preload failed, loading here: " + ex);
            return null;
        }
    }

    // LAUNCH SHIP's YES (0x2109210): the take-off camseq runs 150 vblanks, then a 60-vblank fade to black, the music
    // stopping as it starts; the take-off movie follows
    const int LaunchFadeAt = 150, LaunchFadeTicks = 60;

    // Music.Stop(fade) for a short fade, heard in full: the volume fades now and the player stops once the faded tail
    // has played out (ShipInput). Music.Stop(fade) stops as the fade's last step renders, but the music track renders
    // ~0.28 s ahead (GameAudio's >= 250 ms AudioTrack buffer), so a short fade was flushed at full volume (owner: LAUNCH
    // "sounds like a hard cut"). No stop if other music has started meanwhile.
    const double MusicTail = 0.4;
    double _musicStopIn = -1;
    SeqId _musicStopSeq;

    void FadeOutShipMusic(float seconds)
    {
        MphRead.Music.FadeVolume(0, seconds);
        _musicStopSeq = MphRead.Music.CurrentSeq;
        _musicStopIn = seconds + MusicTail;
    }

    void StepCockpitView(double dt)
    {
        if (_host == null || _cockpitSeq == null) return;
        _cockpitAccum += dt;
        int steps = 0;
        bool launched = false;
        while (_cockpitAccum >= 1 / 60.0 && steps++ < 4)
        {
            _cockpitAccum -= 1 / 60.0;
            try
            {
                if (CameraSequence.Current == _cockpitSeq) _cockpitSeq.Process();
                if (_launchTick < 0 && _cockpitSeq.Flags.TestFlag(CamSeqFlags.Complete))
                {
                    // a revisit's landing is down: the parked loop takes over
                    StartCockpitSeq(CockpitSeqs(_shipPlanet).Loop, loop: true);
                }
                _host.Step(new CampaignInput()); // also ticks Sfx.Update
            }
            catch (Exception ex)
            {
                Log.Warn("MPHCampaign", "cockpit view step: " + ex.Message);
                DropCockpitView();
                if (_launchTick >= 0) FinishLaunch();
                return;
            }
            if (_launchTick >= 0)
            {
                _launchTick++;
                // StopMusic(20) (60 Hz sound frames in mode 12) as a plain fade: Music.Stop(fade) stops the player as the
                // fade's last step renders, but the music track renders ~0.28 s ahead (GameAudio's >= 250 ms AudioTrack
                // buffer), so the stop flushed it at ~85% volume (owner: "a hard cut"). FinishLaunch, 60 ticks on, stops it
                if (_launchTick == LaunchFadeAt) MphRead.Music.FadeVolume(0, 20 / 60f);
                if (_launchTick >= LaunchFadeAt + LaunchFadeTicks)
                {
                    launched = true;
                    break;
                }
            }
        }
        if (_launchTick >= LaunchFadeAt && _shipMenu != null)
        {
            _shipMenu.Fade = (_launchTick - LaunchFadeAt) / (float)LaunchFadeTicks;
        }
        if (launched) FinishLaunch();
    }

    // the take-off is over (faded to black): the planet's take-off movie, then the planet select
    void FinishLaunch()
    {
        MphRead.Music.Stop(); // the fade from LaunchFadeAt has played out by now
        _launchTick = -1;
        DropCockpitView();
        _shipMenu?.Close();
        PlayShipMovie(ShipNavigation.TakeoffMovie(_shipPlanet), () => OpenShip(ShipPage.PlanetSelect));
    }

    // EXIT SHIP: the player takes over the running session, on foot beside the ship, to the room's own music
    void HandCockpitViewToPlayer()
    {
        if (_host != null) MphRead.Music.TryPlayRoomMusic(_host.RoomId, track: 0);
        if (_cockpitSeq != null && CameraSequence.Current == _cockpitSeq) _cockpitSeq.End();
        if (_shipEntity != null) _shipEntity.Hidden = false;
        PlayerEntity.HostHideMainPlayer = false;
        _host?.ReleaseSpawn(); // she spawns now (effect + PLAYER_SPAWN), not when the room loaded under the cockpit
        _cockpitSeq = null;
        _shipEntity = null;
    }

    void DropCockpitView()
    {
        PlayerEntity.HostHideMainPlayer = false;
        _cockpitSeq = null;
        _shipEntity = null;
        if (_host != null)
        {
            try { _host.Dispose(); } catch (Exception ex) { Log.Warn("MPHCampaign", "dispose: " + ex.Message); }
            _host = null;
            ReleaseSceneGpu();
        }
    }

    void OnShipRequest(ShipRequestKind kind)
    {
        if (kind == ShipRequestKind.ExitShip)
        {
            // back out on foot where the ship is parked: no landing movie or camera sequence
            _shipMenu?.Close();
            if (_host != null && _cockpitSeq != null && !_loadoutChanged)
            {
                HandCockpitViewToPlayer();
                CloseShip();
                _paused = false;
                Log.Info("MPHCampaign", "exit ship: on foot");
            }
            else
            {
                DropCockpitView();
                LaunchSession(ShipNavigation.Get(_shipPlanet).LandingRoom, CampaignSaves.Clone(_shipStory!), arriving: false);
            }
            return;
        }
        // LAUNCH SHIP: the cockpit rises off the pad (the take-off camseq) with the popup still up and the menu locked,
        // then fades to black (StepCockpitView) into the take-off movie
        if (_host != null && _cockpitSeq != null)
        {
            StartCockpitSeq(CockpitSeqs(_shipPlanet).Takeoff, loop: false);
            _launchTick = 0;
            Log.Info("MPHCampaign", $"launch: take-off camseq {CockpitSeqs(_shipPlanet).Takeoff}");
            return;
        }
        FinishLaunch();
    }

    // a full-screen movie between ship screens (take-off, the new game's Story Intro); then `after`
    Action? _afterShipMovie;

    // white: the movie fades in from white and out to white (a revisit's landing movie, PlayMovieThenMode fade 5, white)
    void PlayShipMovie(MphRead.Movie movie, Action after, bool white = false)
    {
        _movieWhite = white;
        _movieOutLeft = -1;
        try
        {
            _takeoff = movie == MphRead.Movie.None ? null : MoviePlayer.Open(movie);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", $"movie {movie} unavailable: {ex.Message}");
            _takeoff = null;
        }
        if (_takeoff == null)
        {
            after();
            return;
        }
        Log.Info("MPHCampaign", $"ship: movie {movie}");
        _afterShipMovie = after;
        OpenShip(ShipPage.Takeoff);
    }

    void EndTakeoff()
    {
        _takeoffPresenter?.Stop();
        _takeoff?.Dispose();
        _takeoff = null;
        Action after = _afterShipMovie ?? (() => OpenShip(ShipPage.PlanetSelect));
        _afterShipMovie = null;
        after();
    }

    // A file created on the file select and never played: the new-game intro, then the first planet select.
    // vanilla order (overlay9_0 state 0 -> movie 02 -> state 1, checked on the oracle): the mission briefing (SKIP
    // ends only the briefing), then the Story Intro movie, then the first planet select
    void StartNewGameIntro()
    {
        Log.Info("MPHCampaign", "new game: mission briefing");
        if (_briefing == null)
        {
            _briefing = new ShipBriefing(Paths.FileSystem, _shipTextures);
            _briefing.PlaySound = PlayBriefingSound;
            _briefing.StopScripts = StopUiScripts;
            _briefing.Finished = () =>
            {
                FadeOutShipMusic(5 / 30f); // the briefing's StopMusic(5), 30 Hz sound frames in mode 11
                PlayShipMovie(MphRead.Movie.StoryIntro, () => OpenShip(ShipPage.PlanetSelect));
            };
        }
        // the briefing turns the echo off (preset 1, rev 1 0x2106134)
        DsEcho.SetPreset(1);
        BriefingMusic(); // CampaignAudio.cs: SEQ_NEW_GAME
        _briefing.Start();
        OpenShip(ShipPage.Briefing);
    }

    // after Render(), while the ship screen is open
    void ShipDraw()
    {
        if (_shipPage == ShipPage.Takeoff && _takeoff != null)
        {
            _takeoffPresenter ??= new MoviePresenter();
            _takeoffPresenter.Draw(_takeoff, _width, _height);
        }
        else if (_shipPage == ShipPage.Briefing && _briefing != null)
        {
            _briefing.Build(_width, _height);
            _shipDrawer.Draw(_briefing.DrawList, _width, _height, smooth: false);
        }
        else if (_shipPage == ShipPage.Menu && _shipMenu != null)
        {
            _shipMenu.Build(_width, _height);
            _shipDrawer.Draw(_shipMenu.DrawList, _width, _height, smooth: false);
        }
        else if (_shipPage == ShipPage.PlanetSelect && _planetSelect != null)
        {
            _planetSelect.Build(_width, _height);
            _shipDrawer.Draw(_planetSelect.DrawList, _width, _height, smooth: false);
        }
        DrawVeil();
    }

    // Covers the ship screen white now and runs `then` (the cockpit's blocking room load) on the frame after a white
    // frame was shown; the white lifts over fadeIn seconds once the new view has drawn VeilHoldFrames frames and the
    // white has held minHold seconds since its first full frame
    void VeilThen(Action then, double fadeIn, double minHold = 0)
    {
        _shipPage = ShipPage.Veil;
        _veil = 1;
        _veilDrawn = false;
        _veilThen = then;
        _veilHold = 0;
        _veilFade = fadeIn;
        _veilFadeLeft = 0;
        _veilMinHold = minHold;
    }

    // ShipInput, before the pages: true while a load waits behind the white screen (nothing else runs that frame)
    bool StepVeil(double dt)
    {
        if (_veilThen != null)
        {
            if (!_veilDrawn) return true;
            Action then = _veilThen;
            _veilThen = null;
            then();
            _veilHold = VeilHoldFrames;
            return true;
        }
        if (_veilHold > 0)
        {
            if (_veilHold > 1) _veilHold--;
            else if (_shipClock.Elapsed.TotalSeconds - _veilWhiteAt >= _veilMinHold)
            {
                _veilHold = 0;
                _veilFadeLeft = _veilFade;
            }
        }
        else if (_veilFadeLeft > 0)
        {
            _veilFadeLeft = Math.Max(0, _veilFadeLeft - dt);
            _veil = _veilFade > 0 ? (float)(_veilFadeLeft / _veilFade) : 0;
        }
        else
        {
            _veil = 0;
        }
        return false;
    }

    void DrawVeil()
    {
        float veil = _veil;
        if (_shipPage == ShipPage.Takeoff && _takeoff != null && _movieWhite)
        {
            float fadeIn = 1 - (float)(_takeoff.Position / VeilMovieIn);
            float fadeOut = _movieOutLeft < 0 ? 0 : 1 - (float)(_movieOutLeft / VeilMovieOut);
            veil = Math.Max(veil, Math.Clamp(Math.Max(fadeIn, fadeOut), 0, 1));
        }
        if (veil <= 0) return;
        _veilList ??= new UiDrawList(_shipTextures);
        _veilList.Clear();
        _veilList.Quad(-1, 0, 0, _width, _height, 0, 0, 1, 1, 1, 1, 1, Math.Min(1, veil));
        _shipDrawer.Draw(_veilList, _width, _height, smooth: false);
        if (veil >= 1 && !_veilDrawn)
        {
            _veilDrawn = true;
            _veilWhiteAt = _shipClock.Elapsed.TotalSeconds;
        }
    }

    // OnSurfaceCreated: a new context has none of the old GL objects
    void ShipGlReset()
    {
        _shipDrawer.Init();
        _takeoffPresenter?.Stop();
        _takeoffPresenter = null;
    }

    void OpenShip(ShipPage page)
    {
        _shipPage = page;
        if (page == ShipPage.Menu)
        {
            OpenShipMenu();
        }
        else if (page == ShipPage.PlanetSelect)
        {
            OpenPlanetSelect();
        }
        _shipCursor = 0;
        // swallow whatever is still held from gameplay (a button or the stick needs a release first)
        _sPrevA = _sPrevB = _sPrevUp = _sPrevDown = _sPrevLeft = _sPrevRight = true;
        PushShipText();
    }

    void CloseShip()
    {
        _musicStopIn = -1;
        DropDeepSpace();
        // back in the game: the capture echo is torn down (it never runs in game)
        DsEcho.Stop();
        _shipPage = ShipPage.Closed;
        _shipEnding = false;
        _veil = 0;
        _veilThen = null;
        _veilHold = 0;
        _veilFadeLeft = 0;
        ShipSink?.Invoke("");
    }

    void LaunchSession(string room, StorySave? save, bool arriving = true)
    {
        CloseShip();
        _paused = false;
        _statusCountdown = 0;
        try
        {
            _host = CampaignHost.Start(room, save: save, hunter: _hunter, collectDrawItems: true, viewWidth: _width, viewHeight: _height,
                arriving: arriving);
            Log.Info("MPHCampaign", $"launched {room} as {_hunter}{(arriving ? "" : " (on foot)")}");
        }
        catch (Exception ex)
        {
            _error = $"start failed: {ex.GetType().Name}: {ex.Message}";
            Log.Error("MPHCampaign", ex.ToString());
        }
    }

    // every GPU copy belongs to the finished scene (its list/binding ids restart in the next one)
    void ReleaseSceneGpu()
    {
        foreach (GpuMesh m in _meshes.Values)
        {
            if (m.Vbo != 0)
            {
                GLES30.GlDeleteBuffers(1, new[] { m.Vbo }, 0);
                GLES30.GlDeleteVertexArrays(1, new[] { m.Vao }, 0);
            }
        }
        _meshes.Clear();
        foreach (GpuTexture t in _textures.Values)
        {
            GLES30.GlDeleteTextures(1, new[] { t.Tex }, 0);
        }
        _textures.Clear();
        foreach (int tex in _hudSpriteTex.Values)
        {
            GLES30.GlDeleteTextures(1, new[] { tex }, 0);
        }
        _hudSpriteTex.Clear();
    }

    bool SaveShip()
    {
        if (_saves == null || _shipStory == null)
        {
            return false;
        }
        try
        {
            StorySave copy = CampaignSaves.Clone(_shipStory);
            ShipNavigation.PrepareForSave(copy);
            _saves.Save(_saveSlot, new CampaignSaveFile
            {
                Hunter = _hunter,
                Planet = _shipPlanet,
                SavedAtUtc = DateTime.UtcNow,
                PlaySeconds = PlaySeconds,
                Cleared = _shipCleared,
                KnownAreas = _knownAreas,
                MapConnectors = MapConnectorsForSave(),
                Story = copy
            });
            _shipUnsaved = false;
            _shipNote = $"Progress saved to slot {_saveSlot}.";
            Log.Info("MPHCampaign", $"saved slot {_saveSlot}: {_saves.PathFor(_saveSlot)}");
            return true;
        }
        catch (Exception ex)
        {
            _shipNote = $"Save failed: {ex.Message}";
            Log.Error("MPHCampaign", ex.ToString());
            return false;
        }
    }

    // ---------------------------------------------------------------- the planet select (ROM graphics)

    // the star map of the planets found (StorySave.Areas, as vanilla), the crosshair starting where the ship is
    void OpenPlanetSelect()
    {
        if (_planetSelect == null)
        {
            _planetSelect = new ShipPlanetSelect(Paths.FileSystem, _shipTextures);
            _planetSelect.Log = m => Log.Info("MPHCampaign", m);
            _planetSelect.PlaySound = PlayUiSound;
            _planetSelect.StopScripts = StopUiScripts;
            _planetSelect.PlayLoop = PlayUiLoop;
            _planetSelect.StopLoop = StopUiLoop;
            _planetSelect.ReleaseLoop = StopUiLoop;
            _planetSelect.LoopAlive = UiLoopAlive;
            _planetSelect.SetEcho = DsEcho.SetPreset;
            _planetSelect.Land = LandShip;
            _planetSelect.KnownAreasChanged = known => _knownAreas = known;
        }
        _planetSelect.RedrawArt = MphRecomp.Config.RecompSettings.Load(_gunSettingsPath).RedrawArt;
        while (_shipTouches.TryDequeue(out _)) { }
        string[] names = ShipNavigation.Planets.Select(p => p.Name).ToArray();
        _planetSelect.Open(_shipStory?.Areas ?? 0, _knownAreas, names, (int)_shipPlanet);
        StartDeepSpace();
        _deepSpace?.ShowOubliette(_planetSelect.OublietteFound);
        // after the deep space view: its new Scene runs Music.Init, which stopped a SEQ_SHIP started before it
        PlanetSelectMusic(); // CampaignAudio.cs: SEQ_SHIP, as the game's planet-select enter
    }

    // only with no campaign session alive (one MphRead scene at a time); its GPU copies go with it
    void StartDeepSpace()
    {
        if (_deepSpace != null || _host != null) return;
        try
        {
            _deepSpace = DeepSpaceView.Start(_width, _height);
            Log.Info("MPHCampaign", "planet select: deep space view");
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", "deep space view unavailable: " + ex);
            _deepSpace = null;
        }
    }

    // the window's view: the planet select's camera (a spring after the crosshair) moves the deep-space model
    void StepDeepSpace(double dt)
    {
        if (_deepSpace == null || _planetSelect == null) return;
        try
        {
            _deepSpace.Look(128 - _planetSelect.CameraX, 96 - _planetSelect.CameraY);
            _deepSpace.Step(dt, _width, _height);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", "deep space step: " + ex.Message);
            DropDeepSpace();
        }
    }

    void DropDeepSpace()
    {
        if (_deepSpace == null) return;
        try { _deepSpace.Dispose(); } catch (Exception ex) { Log.Warn("MPHCampaign", "deep space dispose: " + ex.Message); }
        _deepSpace = null;
        ReleaseSceneGpu();
    }

    // LAND SHIP: the chosen planet's landing room, arriving (its landing movie, then the landing camera sequence)
    void LandShip(int index)
    {
        StopShipMusic(); // CampaignAudio.cs: leaving the planet select stops SEQ_SHIP
        _planetSelect?.Close();
        _shipPlanet = (Planet)index;
        if (_shipNotStarted)
        {
            // the first landing of a new file: from now on it continues at the planet select
            _shipNotStarted = false;
            SaveShip();
        }
        PlanetInfo planet = ShipNavigation.Get(_shipPlanet);
        if (_shipStory != null && _shipStory.CheckVisitedRoom(planet.LandingRoomId))
        {
            // a revisit (LAND, rev 1 ov0 0x2105398: the landing room's VisitedRooms bit -> mode 12, the ship on the
            // ground): the landing movie, then the cockpit coming down onto the pad with the in-ship menu usable at
            // once and no save prompt (LAND clears flag 0x1000); a first visit lands in the game (its own movie + camseq)
            DropDeepSpace();
            _cockpitLanding = true;
            Log.Info("MPHCampaign", $"land: revisit {planet.LandingRoom}");
            PreloadCockpitView(planet);
            PlayShipMovie(ShipNavigation.LandingMovie(_shipPlanet),
                () => VeilThen(() => OpenShip(ShipPage.Menu), VeilLandIn, VeilLandHold), white: true);
            return;
        }
        LaunchSession(planet.LandingRoom, CampaignSaves.Clone(_shipStory!));
    }

    // ---------------------------------------------------------------- input

    bool _shipBackTap; // Android's Back (CampaignActivity.cs BackFrame): one B press on this frame

    void ShipInput()
    {
        PadState pad = Pad;
        bool a = pad[Keycode.ButtonA], b = pad[Keycode.ButtonB] || _shipBackTap;
        _shipBackTap = false;
        bool up = pad.HatY <= -0.5f || pad[Keycode.DpadUp] || pad.Ly <= -0.6f;
        bool down = pad.HatY >= 0.5f || pad[Keycode.DpadDown] || pad.Ly >= 0.6f;
        bool left = pad.HatX <= -0.5f || pad[Keycode.DpadLeft] || pad.Lx <= -0.6f;
        bool right = pad.HatX >= 0.5f || pad[Keycode.DpadRight] || pad.Lx >= 0.6f;
        bool pressA = a && !_sPrevA, pressB = b && !_sPrevB;
        int move = (down && !_sPrevDown || right && !_sPrevRight ? 1 : 0) - (up && !_sPrevUp || left && !_sPrevLeft ? 1 : 0);
        int moveX = (right && !_sPrevRight ? 1 : 0) - (left && !_sPrevLeft ? 1 : 0);
        int moveY = (up && !_sPrevUp ? 1 : 0) - (down && !_sPrevDown ? 1 : 0);
        _sPrevA = a; _sPrevB = b; _sPrevUp = up; _sPrevDown = down; _sPrevLeft = left; _sPrevRight = right;
        if (!_shipClock.IsRunning) _shipClock.Start();
        double now = _shipClock.Elapsed.TotalSeconds, dt = Math.Min(now - _shipLast, 0.1);
        _shipLast = now;
        // UI scripts (BIG_SLIDE_SCR, SMALL_POP_UP_SCR, ENTER_SHIP_SCR, PLANET_FOUND_SCR, LAND_SHIP_SCR, the take-off's
        // LAUNCH_SHIP_YES_SCR ...) only advance in AndroidSfxPlayer.Update, which otherwise runs inside
        // CampaignHost.Step: on any ship page that doesn't step the host (briefing, planet select, the take-off movie,
        // a menu without its cockpit view) they never sounded. Tick it here instead -- once, never on top of the host.
        bool hostTicksSfx = _shipPage == ShipPage.Menu && _host != null && _cockpitSeq != null;
        if (!hostTicksSfx) _sfx?.Update((float)dt);
        // CampaignAudio.cs: Music.UpdateMusic, likewise the host's job -- not while a cockpit room loads on a worker
        // (Music.Init runs there) or waits to be adopted (its load queued the room's track, the cockpit plays its own)
        if (!hostTicksSfx && _cockpitPreload == null) ShipMusicTick();
        if (_musicStopIn >= 0 && (_musicStopIn -= dt) < 0 && MphRead.Music.CurrentSeq == _musicStopSeq)
        {
            MphRead.Music.Stop(); // FadeOutShipMusic: the faded tail has played out
        }
        if (StepVeil(dt)) return;
        _shipTickAccum = Math.Min(_shipTickAccum + dt, 4 / 60.0);
        int ticks = 0;
        for (; _shipTickAccum >= 1 / 60.0; ticks++) _shipTickAccum -= 1 / 60.0;
        if (_shipPage == ShipPage.Menu && _shipMenu != null)
        {
            while (_shipTouches.TryDequeue(out var t))
            {
                if (t.Kind == 0) _shipMenu.Touch(t.X, t.Y);
                else if (t.Kind == 1) _shipMenu.TouchMove(t.X, t.Y);
                else _shipMenu.TouchUp(t.X, t.Y);
            }
            StepCockpitView(dt);
            if (moveX != 0 || moveY != 0) _shipMenu.Navigate(moveX, moveY);
            if (pressA) _shipMenu.Press(MenuKeys.A);
            if (pressB) _shipMenu.Press(MenuKeys.B);
            for (int i = 0; i < ticks && _shipPage == ShipPage.Menu; i++) _shipMenu.Tick();
            return;
        }
        if (_shipPage == ShipPage.Briefing && _briefing != null)
        {
            bool held = false;
            while (_shipTouches.TryDequeue(out var t))
            {
                if (t.Kind == 0) _briefing.Touch(t.X, t.Y);
                held = t.Kind != 2;
            }
            _briefing.Fast = a || held;
            // vanilla skips only on the touch screen's SKIP (0x2105b58; B does nothing there); Start is the recomp's pad
            // shortcut for it
            if (pad[Keycode.ButtonStart]) _briefing.Skip();
            for (int i = 0; i < ticks && _shipPage == ShipPage.Briefing; i++) _briefing.Tick();
            return;
        }
        if (_shipPage == ShipPage.PlanetSelect && _planetSelect != null)
        {
            while (_shipTouches.TryDequeue(out var t))
            {
                if (t.Kind == 0) _planetSelect.Touch(t.X, t.Y);
                else if (t.Kind == 1) _planetSelect.TouchMove(t.X, t.Y);
                else _planetSelect.TouchUp(t.X, t.Y);
            }
            _planetSelect.Hold((right ? 1 : 0) - (left ? 1 : 0), (up ? 1 : 0) - (down ? 1 : 0));
            if (pressA) _planetSelect.Press(MenuKeys.A);
            for (int i = 0; i < ticks && _shipPage == ShipPage.PlanetSelect; i++) _planetSelect.Tick();
            if (_shipPage == ShipPage.PlanetSelect) StepDeepSpace(dt);
            return;
        }
        if (_shipPage == ShipPage.Takeoff)
        {
            while (_shipTouches.TryDequeue(out _)) { }
            if (_takeoff == null) { EndTakeoff(); return; }
            if (pressA || pressB) _takeoff.Skip();
            _takeoff.Update(dt);
            if (_takeoff.Finished && _movieWhite)
            {
                // vanilla holds the last frame and fades it out to white (10 vblanks) before the next mode loads
                _movieOutLeft = _movieOutLeft < 0 ? VeilMovieOut : _movieOutLeft - dt;
                if (_movieOutLeft <= 0) EndTakeoff();
            }
            else if (_takeoff.Finished) EndTakeoff();
            return;
        }
        if (move == 0 && !pressA && !pressB)
        {
            return;
        }
        switch (_shipPage)
        {
        case ShipPage.SavePrompt:
            if (move != 0)
            {
                _shipCursor = 1 - _shipCursor;
            }
            if (pressA && _shipCursor == 0)
            {
                SaveShip();
                OpenShip(ShipPage.PlanetSelect);
                return;
            }
            if (pressB || pressA)
            {
                _shipNote = "Not saved.";
                OpenShip(ShipPage.PlanetSelect);
                return;
            }
            break;
        case ShipPage.GameOver:
        {
            List<ShipRow> rows = GameOverRows();
            _shipCursor = NextSelectable(rows, _shipCursor, move);
            if (pressA)
            {
                ShipRow row = rows[_shipCursor];
                if (row.Action == ShipAction.Load)
                {
                    if (_saves!.TryLoad(_saveSlot, out CampaignSaveFile? file, out string? error))
                    {
                        AdoptSave(file!);
                        _shipNote = $"Loaded slot {_saveSlot}.";
                        OpenShip(ShipPage.PlanetSelect);
                        return;
                    }
                    _shipNote = $"Slot {_saveSlot} could not be read: {error ?? "empty"}";
                }
                if (row.Action == ShipAction.NewGame)
                {
                    _shipStory = null;
                    _knownAreas = 0;
                    _shipCleared = false;
                    _shipUnsaved = false;
                    _playFrames = 0;
                    _shipPlanet = Planet.CelestialArchives;
                    LaunchSession(ShipNavigation.Get(Planet.CelestialArchives).LandingRoom, save: null);
                    return;
                }
                if (row.Action == ShipAction.Quit)
                {
                    ExitSink?.Invoke();
                    return;
                }
            }
            break;
        }
        }
        PushShipText();
    }

    enum ShipAction { None, Quit, Load, NewGame }

    sealed class ShipRow
    {
        public string Text = "";
        public ShipAction Action;
        public bool Selectable => Action != ShipAction.None;
    }

    static int NextSelectable(List<ShipRow> rows, int cursor, int move)
    {
        if (rows.Count == 0)
        {
            return 0;
        }
        cursor = Math.Clamp(cursor, 0, rows.Count - 1);
        if (move == 0)
        {
            return rows[cursor].Selectable ? cursor : NextSelectable(rows, cursor, 1);
        }
        for (int i = 1; i <= rows.Count; i++)
        {
            int next = ((cursor + move * i) % rows.Count + rows.Count) % rows.Count;
            if (rows[next].Selectable)
            {
                return next;
            }
        }
        return cursor;
    }

    List<ShipRow> GameOverRows()
    {
        var rows = new List<ShipRow>();
        if (_saves != null && _saves.Exists(_saveSlot))
        {
            rows.Add(new ShipRow { Text = $"LOAD LAST SAVE (slot {_saveSlot}: {SlotSummary()})", Action = ShipAction.Load });
        }
        rows.Add(new ShipRow { Text = "NEW GAME (Celestial Archives)", Action = ShipAction.NewGame });
        rows.Add(new ShipRow { Text = "QUIT", Action = ShipAction.Quit });
        return rows;
    }

    string SlotSummary()
    {
        if (_saves == null || !_saves.TryLoad(_saveSlot, out CampaignSaveFile? file, out _))
        {
            return "empty";
        }
        return $"{ShipNavigation.Get(file!.Planet).Name}, {file.Hunter}, {file.Story.GetCompletionPercentage()}%, "
            + $"{TimeSpan.FromSeconds(file.PlaySeconds):h\\:mm}, saved {file.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    // ---------------------------------------------------------------- text

    void PushShipText()
    {
        ShipSink?.Invoke(ShipText());
    }

    string ShipText()
    {
        if (_shipPage is ShipPage.Menu or ShipPage.Takeoff or ShipPage.Briefing or ShipPage.PlanetSelect or ShipPage.Veil)
        {
            return ""; // drawn in GL
        }
        var sb = new StringBuilder();
        switch (_shipPage)
        {
        case ShipPage.SavePrompt:
            sb.AppendLine(_shipEnding ? "MISSION COMPLETE" : "HUNTER GUNSHIP");
            sb.AppendLine();
            if (_shipEnding && _shipStory != null)
            {
                sb.AppendLine($"Completion {_shipStory.GetCompletionPercentage()}%   play time {TimeSpan.FromSeconds(PlaySeconds):h\\:mm}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"Docked at {ShipNavigation.Get(_shipPlanet).Name}. Energy and ammo restored.");
                sb.AppendLine();
            }
            sb.AppendLine("SAVE YOUR PROGRESS?");
            sb.AppendLine($"slot {_saveSlot}: {(_saves != null && _saves.Exists(_saveSlot) ? SlotSummary() + " -- will be replaced" : "empty")}");
            sb.AppendLine();
            sb.AppendLine($"  {(_shipCursor == 0 ? ">" : " ")} YES");
            sb.AppendLine($"  {(_shipCursor == 1 ? ">" : " ")} NO");
            sb.AppendLine();
            sb.Append("A select   B no");
            break;
        case ShipPage.GameOver:
        {
            sb.AppendLine("GAME OVER");
            sb.AppendLine();
            List<ShipRow> rows = GameOverRows();
            _shipCursor = NextSelectable(rows, _shipCursor, 0);
            for (int i = 0; i < rows.Count; i++)
            {
                sb.AppendLine($"  {(i == _shipCursor ? ">" : " ")} {rows[i].Text}");
            }
            sb.AppendLine();
            sb.Append("A select");
            break;
        }
        }
        if (_shipNote.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(_shipNote);
        }
        return sb.ToString();
    }
}
