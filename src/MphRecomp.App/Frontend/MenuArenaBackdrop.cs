using System;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;

namespace MphRecomp.App;

// LOOK > MENUS ARENA BACKGROUND (owner 2026-11-20): in place of the Samus menu art, a random multiplayer arena (vanilla
// and imported) drawn live from a fixed camera (CampaignRenderer.MenuBackdrop.cs). Sharp behind TOUCH TO START (page
// 13); past it, blurred and darkened behind the menus ("blur + darken only", no tint cycle). The menus' own music plays
// as always: the arena makes no sound. GL thread only; FrontendRenderer owns it and drops it before a match starts.
// The room builds on a worker from app start (BeginLoad; its scene is headless, no GL), GoLive takes it and its GPU copies
// go up during the startup screens (Warm), so it's live by the title (owner 11-21: "it seems all instant now", so no
// still pictures in front of it any more).
internal sealed class MenuArenaBackdrop : IDisposable
{
    const int TitlePage = 13;
    const float BlurInRate = 2.0f; // per second, as the PRIME map's blur (PrimeMapBlur)
    const float Darken = 0.55f; // the black laid over the blurred arena (full blur)

    CampaignRenderer? _renderer;
    // down to 1/8 and back up to 1/4 (the PRIME map's 1/32 was "far too intense" here, owner 11-20)
    readonly PrimeMapBlur _blur = new(levels: 3, shown: 1);
    readonly UiDrawList _list;
    readonly int _blurTex;
    float _blurAmount;
    static bool s_oldPicturesGone;

    public string Arena { get; }
    // dev builds (the preview sheet, SecondHunt.Desktop MPH_MENU_ARENA / MPH_MENU_SPAWN): this arena, this spawn's view
    public static string? DevArena;
    public static int DevSpawn = -1;

    readonly string? _importRoot; // the data folder, beside RECOMP SETTINGS (holiday mode: CampaignRenderer.BackdropBegin)

    MenuArenaBackdrop(UiTextureCache textures, string arena, string? importRoot)
    {
        _list = new UiDrawList(textures);
        _blurTex = textures.GetOrAdd("bg/arena-blur", () => (1, 1, new byte[4])).Id;
        Arena = arena;
        _importRoot = importRoot;
    }

    // a random arena (never First Hunt's); the room isn't loaded yet (BeginLoad), unless a game leaving for the menus
    // began it already (BeginEarly): that one. Null if there's no arena
    public static MenuArenaBackdrop? Prepare(string? importRoot, UiTextureCache textures, Random rng)
    {
        if (s_early is { } early) return new MenuArenaBackdrop(textures, early.Arena, importRoot);
        string? arena = PickArena(importRoot, rng);
        return arena == null ? null : new MenuArenaBackdrop(textures, arena, importRoot);
    }

    static string? PickArena(string? importRoot, Random rng)
    {
        MphRecomp.Arenas.EchoesArena.ImportRoot = importRoot;
        MenuBackdropShots.LoadPicked(importRoot); // the owner's shots from the dev picker (CampaignRenderer.MenuShotPick.cs)
        DeleteOldPictures(importRoot);
        var pool = MphRecomp.Multiplayer.MatchArenas.All
            .Where(r => !r.FirstHunt && !MphRecomp.Multiplayer.MatchSettings.IsFirstHuntArena(r.Name)
                && MphRecomp.Arenas.FirstHuntArena.Find(r.Name) == null) // (FH MP5, FH REGULATOR ...: dev-only rooms)
            .Select(r => r.Name)
            .ToList();
        if (pool.Count == 0) return null;
        return DevArena is { Length: > 0 } dev ? dev : pool[rng.Next(pool.Count)];
    }

    // ---- begun by a game leaving for the menus (CampaignRenderer.LeaveForMenus.cs) ----
    // At launch the room builds on a worker while the app starts, so the title has it at once. Back from a game nothing
    // hid that wait: the game's last picture sat frozen while the menus loaded it (owner 11-21: "i want to have everything
    // instant like the initial menu map loads when you load the game"). So the game, the moment QUIT's YES is pressed,
    // drops its own scene and starts this build; it runs during YES's flash and the switch to the menus, and the menus'
    // first frame takes it (Prepare/BeginLoad) instead of starting one.
    sealed record Early(string Arena, System.Threading.Tasks.Task<MphRecomp.Campaign.CampaignHost> Load, long Started);
    static Early? s_early;
    static readonly Random s_earlyRng = new();

    public static bool EarlyPending => s_early != null;

    // ---- read ahead: the game's pause menu opened (CampaignRenderer.LeaveAhead) ----
    // YES gives the early build only ~0.3 s of flash, and its slowest parts need no MphRead scene: a big snow room's
    // cached snow (HolidayGround.ReadAhead, 160+ ms on the Odin) and an imported arena's HD bundle (ReadArenaHdAhead,
    // ~80 ms). So the next backdrop is picked when the pause opens and those two read on workers while the menu is up (the
    // game is paused: nothing to slow down); BeginEarly takes that arena, the worker and the menus' renderer their reads.
    static string? s_ahead;

    // the game's GL thread: false when the setting is off or there's no arena
    public static bool ReadAhead(string settingsPath)
    {
        if (!MphRecomp.Config.RecompSettings.Load(settingsPath).MenuArenaBackdrop) return false;
        s_ahead ??= PickArena(Path.GetDirectoryName(settingsPath), s_earlyRng);
        if (s_ahead == null) return false;
        if (MphRead.Metadata.GetRoomByName(s_ahead).Item1 is MphRead.RoomMetadata meta) MphRecomp.Holiday.HolidayGround.ReadAhead(meta);
        CampaignRenderer.ReadArenaHdAhead(s_ahead);
        Log.Info("MPHMenu", $"arena background {s_ahead} picked as the pause opened, read ahead");
        return true;
    }

    // the pause closed without leaving, a new game started, or the menus' backdrop is live: what was read goes (the
    // arena picked stays for the next pause)
    public static void DropAhead()
    {
        MphRecomp.Holiday.HolidayGround.DropAhead();
        CampaignRenderer.DropArenaHdAhead();
    }

    // the game's GL thread, its scene already gone: false when the setting is off or there's no arena
    public static bool BeginEarly(string settingsPath, string filesDir, int width, int height)
    {
        DropEarly();
        string? importRoot = Path.GetDirectoryName(settingsPath);
        if (!MphRecomp.Config.RecompSettings.Load(settingsPath).MenuArenaBackdrop) return false;
        string? arena = s_ahead ?? PickArena(importRoot, s_earlyRng);
        s_ahead = null;
        if (arena == null) return false;
        Directory.SetCurrentDirectory(filesDir); // (as CampaignRenderer.BackdropBegin)
        MphRead.Paths.UpdatePaths();
        MphRead.Paths.ChooseMphPath();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        s_early = new Early(arena, MphRecomp.Campaign.CampaignHost.BeginMenuBackdrop(arena, width, height), started);
        Log.Info("MPHMenu", $"arena background {arena} loading as the game leaves for the menus");
        return true;
    }

    // one begun early that nobody took (a new game starts, the setting went off): it finishes, then goes
    public static void DropEarly()
    {
        var early = s_early;
        if (early == null) return;
        s_early = null;
        MphRecomp.Campaign.CampaignHost.WaitMenuBackdrop();
        try { if (early.Load.IsCompletedSuccessfully) early.Load.Result.Dispose(); }
        catch (Exception ex) { Log.Warn("MPHMenu", "early arena background dispose: " + ex.Message); }
        Log.Info("MPHMenu", $"arena background {early.Arena} begun early, not taken: dropped");
    }

    // dev builds 734-737 kept each arena's view as a still picture in <data>/cache/menu_bg (~1.7 MB each): gone, once a
    // run (never in a release, so this can go after the next one)
    static void DeleteOldPictures(string? importRoot)
    {
        if (s_oldPicturesGone || importRoot is not { Length: > 0 }) return;
        s_oldPicturesGone = true;
        string dir = Path.Combine(importRoot, "cache", "menu_bg");
        try
        {
            if (!Directory.Exists(dir)) return;
            Directory.Delete(dir, recursive: true);
            Log.Info("MPHMenu", "menu backdrop: old pictures deleted");
        }
        catch (Exception ex)
        {
            Log.Warn("MPHMenu", "menu backdrop: old pictures not deleted: " + ex.Message);
        }
    }

    public bool IsLive => _frozen || (_renderer != null && !_loading);
    public bool Frozen => _frozen;
    // a blurred picture is up (past the title) that Freeze can hold still (FrontendRenderer.OnRequest)
    public bool CanHold => (_frozen || (_renderer != null && !_loading)) && _lastBlurred != 0 && _blurAmount > 0;
    public bool TookEarly { get; private set; } // its room was begun by the game leaving (BeginEarly)
    public bool Loading => _loading;
    public bool LoadDone => _loading && _renderer!.BackdropLoaded;
    public bool Alive => _renderer == null || _loading || _renderer.BackdropAlive;

    bool _loading;
    bool _warm;
    bool _frozen;
    int _lastBlurred;

    // GL thread, START's lead-in (FrontendRenderer.OnLeadIn): the scene goes (the match builds meanwhile: one MphRead
    // scene at a time) and its last blurred picture stays, still, under the lead-in's pages until the menus leave
    public void Freeze()
    {
        if (_frozen) return;
        _frozen = true;
        _renderer?.BackdropDispose(); // (waits for a load still building)
        _renderer = null;
        _loading = false;
    }

    // GL thread: the room starts loading on a worker (the slow part, a second or so; owner 11-21: "everything is just
    // sitting waiting to be handed off"), from app start, whatever the startup screens do
    public void BeginLoad(string filesDir, int width, int height)
    {
        var renderer = CampaignRenderer.ForMenuBackdrop(filesDir);
        if (_importRoot != null) renderer.SettingsPath = Path.Combine(_importRoot, "recomp_settings.json"); // (as CampaignLaunch)
        renderer.OnSurfaceCreated();
        var early = s_early is { } e && e.Arena == Arena ? e : null;
        s_early = null;
        TookEarly = early != null;
        renderer.BackdropBegin(Arena, width, height, early?.Load, early?.Started ?? 0);
        _renderer = renderer;
        _loading = true;
    }

    // GL thread, once LoadDone (or to wait for it: the menus leaving the title touch MphRead): the room taken; false if
    // it failed
    public bool GoLive(Random rng)
    {
        if (_renderer == null || !_loading) return _renderer != null;
        if (!_renderer.BackdropLoaded) MphRecomp.Campaign.CampaignHost.WaitMenuBackdrop();
        _loading = false;
        bool ok = _renderer.BackdropFinish(Arena, rng, DevSpawn);
        DropAhead(); // (anything read ahead and not taken)
        if (!ok)
        {
            _renderer = null;
            return false;
        }
        return true;
    }

    // GL thread, each frame it's live but not shown (the startup screens): its GPU copies go up a few ms a frame
    public void Warm()
    {
        if (!_warm && _renderer != null && !_loading) _warm = _renderer.BackdropWarm();
    }

    // before the menus' draw list, once live: the arena, then (past the title) its blurred copy and the darken over it
    public void Draw(double dt, int width, int height, int page, UiGlDrawer drawer)
    {
        if (!IsLive) return; // (not live yet: the Samus art stays, FrontendRenderer)
        if (_frozen)
        {
            if (_lastBlurred == 0 || _blurAmount <= 0) return;
            _list.Clear();
            drawer.UseGlTexture(_blurTex, _lastBlurred);
            _list.Quad(_blurTex, 0, 0, width, height, 0, 1, 1, 0, 1, 1, 1, _blurAmount);
            _list.Quad(-1, 0, 0, width, height, 0, 0, 1, 1, 0, 0, 0, Darken * _blurAmount);
            drawer.Draw(_list, width, height, false);
            return;
        }
        _renderer!.BackdropFrame(dt, width, height);
        if (!_renderer.BackdropAlive) return;
        float target = page == TitlePage ? 0 : 1;
        float step = (float)dt * BlurInRate;
        _blurAmount = _blurAmount < target ? Math.Min(target, _blurAmount + step) : Math.Max(target, _blurAmount - step);
        if (_blurAmount <= 0) return;
        _list.Clear();
        int blurred = _lastBlurred = _blur.Capture(width, height);
        if (blurred != 0)
        {
            drawer.UseGlTexture(_blurTex, blurred);
            _list.Quad(_blurTex, 0, 0, width, height, 0, 1, 1, 0, 1, 1, 1, _blurAmount); // (rows bottom up)
        }
        _list.Quad(-1, 0, 0, width, height, 0, 0, 1, 1, 0, 0, 0, Darken * _blurAmount);
        drawer.Draw(_list, width, height, false);
    }

    public void Dispose()
    {
        _renderer?.BackdropDispose(); // (waits for a load still building)
        _renderer = null;
        _loading = false;
    }
}
