using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.FirstHunt;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The whole First Hunt game (docs/FIRST_HUNT_PORT.md) on the campaign screen: launch arg fhlevel = a First Hunt level's
// room name (e.g. "Level SP Regulator"), from the game imported under <data>/firsthunt/AMFE (FirstHuntGame). P0: Samus
// alone in the level, first person, with our controls; the modes' rules come in later phases.
internal sealed partial class CampaignRenderer
{
    string? _fhLevel;
    // launched from First Hunt's own menus (FirstHuntActivity, arg fhmenu): a training game's end hands its result back
    // (<saves>/firsthunt_last.txt) and closes this screen
    bool _fhFromMenu;

    // CampaignLaunch.Create, after the match args: dev only until the game has its EXTRAS entry
    void ConfigureFirstHunt(LaunchArgs args)
    {
        if (MphRecomp.Config.RecompSettings.PublicBuild) return;
        _fhLevel = args.Get("fhlevel");
        _fhFromMenu = _fhLevel != null && args.Get("fhmenu") != null;
        if (_fhLevel != null)
        {
            // the cart's own arm cannon, body and ball (PlayerFirstHunt.cs), never RECOMP SETTINGS' HD suit and gun:
            // nothing for StartGearLoad to load (todo: a suit changed in the pause menu still hot-swaps the HD gear in,
            // ReloadGunFromSettings)
            _gunDir = null;
            _suitId = null;
        }
        if (_fhLevel != null && args.Get("fhcam") is string cam)
        {
            float[] v = cam.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            _fhCamPos = new Vector3(v[0], v[1], v[2]);
            _fhCamFwd = new Vector3(v[3], v[4], v[5]);
            _fhCamFov = v.Length > 6 ? v[6] : 0;
        }
    }

    // dev fhcam=x,y,z,fx,fy,fz[,fovY]: a fixed render-only view at the cart's camera pose (BizHawk RAM: position
    // 0x208b594, facing 0x208b588) for the cart-vs-ours comparison shots; Samus is placed under it once she has spawned,
    // so the room's culling, triggers and enemies are as they'd be with her there
    Vector3? _fhCamPos;
    Vector3 _fhCamFwd;
    float _fhCamFov;
    bool _fhCamPlaced;

    bool UpdateFirstHuntPoseCam(Scene scene, PlayerEntity p)
    {
        if (_fhCamPos is not Vector3 pos || _host == null) return false;
        if (!_fhCamPlaced && p.Health > 0)
        {
            var flat = new Vector3(_fhCamFwd.X, 0, _fhCamFwd.Z);
            _host.PlacePlayer(pos - Vector3.UnitY * 0.95f, flat.LengthSquared > 1e-6f ? flat.Normalized() : -Vector3.UnitZ);
            _fhCamPlaced = true;
        }
        scene.HostCameraOffset = Vector3.Zero;
        scene.HostView = new HostView(pos, pos + _fhCamFwd, Vector3.UnitY, _fhCamFov);
        return true;
    }

    // OnSurfaceChanged: start the First Hunt level instead of a match or campaign room
    bool TryStartFirstHunt(int width, int height)
    {
        if (_fhLevel == null)
        {
            // a deathmatch from First Hunt's lobby pages (P5, net=host|client): a LAN match (TryStartMatch) in a First
            // Hunt arena, which needs the game registered and its sounds first
            if (_matchSettings != null && MphRecomp.Multiplayer.MatchSettings.IsFirstHuntArena(_matchSettings.Arena))
            {
                if (!FirstHuntGame.Register(MphRecomp.Arenas.EchoesArena.ImportRoot))
                {
                    throw new InvalidOperationException("First Hunt isn't imported (" + FirstHuntGame.Dir(MphRecomp.Arenas.EchoesArena.ImportRoot ?? "") + ")");
                }
                _sfx?.LoadFirstHunt();
                Log.Info("MPHFirstHunt", $"First Hunt deathmatch in {_matchSettings.Arena}");
            }
            return false;
        }
        if (!FirstHuntGame.Register(MphRecomp.Arenas.EchoesArena.ImportRoot))
        {
            throw new InvalidOperationException("First Hunt isn't imported (" + FirstHuntGame.Dir(MphRecomp.Arenas.EchoesArena.ImportRoot ?? "") + ")");
        }
        _sfx?.LoadFirstHunt(); // the cart's sound banks (P4), before the level's first sounds
        _host = CampaignHost.StartFirstHunt(_fhLevel, collectDrawItems: true, viewWidth: width, viewHeight: height);
        Log.Info("MPHFirstHunt", $"First Hunt level {_fhLevel}");
        Action? over = FirstHuntMission.HostOver;
        bool done = false;
        FirstHuntMission.HostOver = () =>
        {
            if (done) return; // (called every frame while the end clock is 0)
            done = true;
            FhTrainingResult? result = RecordFirstHunt();
            if (_fhFromMenu && _saves != null)
            {
                try
                {
                    if (result != null) File.WriteAllText(FirstHuntMenuRenderer.ResultFile(_saves.Directory), result.ToLine());
                }
                catch (Exception ex)
                {
                    Log.Error("MPHFirstHunt", "First Hunt result: " + ex.Message);
                }
                ExitSink?.Invoke();
                return;
            }
            over?.Invoke();
        };
        return true;
    }

    // a deathmatch's end (P5, TryEndMatch): its results for First Hunt's menus (<saves>/firsthunt_last.txt), whose results
    // page shows them and keeps the totals
    void RecordFirstHuntMatch()
    {
        if (!PlayerEntity.HostFirstHunt || _matchSettings == null || _saves == null) return;
        try
        {
            FhMatchResult result = FhMatchResult.Capture(_matchSettings.Players.Count, _matchSettings.LocalSeat, _netSession?.IsHost ?? true);
            File.WriteAllText(FirstHuntMenuRenderer.ResultFile(_saves.Directory), result.ToLine());
            Log.Info("MPHFirstHunt", "First Hunt deathmatch result: " + result.ToLine());
        }
        catch (Exception ex)
        {
            Log.Error("MPHFirstHunt", "First Hunt deathmatch result: " + ex.Message);
        }
    }

    // a training game's end (P4, the results fill 0x203f898): its run against the records in <data>/saves/firsthunt.sav,
    // the cart's own EEPROM format (FirstHuntSave). The results and records screens come with the front end.
    FhTrainingResult? RecordFirstHunt()
    {
        if (!FirstHuntMission.IsTraining || _saves == null) return null;
        try
        {
            string path = Path.Combine(_saves.Directory, "firsthunt.sav");
            FirstHuntSave save = FirstHuntSave.Load(path);
            int slot = PlayerEntity.MainPlayerIndex;
            (int rank, bool movie, bool changed) = save.RecordTraining(FirstHuntMission.Mode, FirstHuntMission.Result != FhResult.Complete,
                FirstHuntMission.Score[slot], FirstHuntMission.Elapsed / 2, FirstHuntMission.Kills[slot]); // todo: FPS stuff
            if (changed) save.Save();
            Log.Info("MPHFirstHunt", $"First Hunt {FirstHuntMission.Mode} {FirstHuntMission.Result}: score {FirstHuntMission.Score[slot]}, "
                + $"rank {rank + 1}{(movie ? ", movie unlocked" : "")}{(changed ? ", saved" : "")}");
            return new FhTrainingResult(FirstHuntMission.Mode, FirstHuntMission.Result != FhResult.Complete, FirstHuntMission.Score[slot],
                FirstHuntMission.Elapsed / 2, FirstHuntMission.Kills[slot], rank, movie);
        }
        catch (Exception ex)
        {
            Log.Error("MPHFirstHunt", "First Hunt save: " + ex.Message); // (the cart's popup 0x15 "Unable to save")
            return null;
        }
    }
}
