using MphRead;
using MphRecomp.Campaign;
using OpenTK.Mathematics;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace MphRecomp.App;

// A match built during START's lead-in (MatchPrebuild.cs): OnSurfaceChanged's start split in two. PrebuildStart runs on
// the worker (everything with no GL and no sound: paths, the gear and skin loads, TryStartMatch); FinishPrebuilt runs in
// OnSurfaceChanged (the audio, the room's music, the gear's, skins' and arena's GL parts), as the old start's tail did.
internal sealed partial class CampaignRenderer
{
    bool _prebuilt; // started on MatchPrebuild's worker: OnSurfaceChanged finishes it
    // MatchPrebuild's worker, around Create: a local match, built while the lobby's own LAN host session (no joiners) is
    // still open; StartMatch ends that session before the match screen, so Create's mods check doesn't count it
    [System.ThreadStatic] internal static bool t_leadInBuild;
    (int Room, int Track)? _prebuiltMusic;

    // The room's music, held for the match screen: SceneSetup's TryPlayRoomMusic runs inside the room load, and the
    // menus' own music is still playing then (the lead-in). Taking the call here (and nothing else of Music's) leaves
    // MPH's music state as it was before the call; FinishPrebuilt makes it again, with the screen's own players set up.
    sealed class HeldRoomMusic : Music.IRoomMusicHost
    {
        public (int, int)? Room;
        public int Later; // calls after it (none are expected: logged)

        public bool PlayRoom(int roomId, int track)
        {
            if (Room == null) Room = (roomId, track);
            else Later++;
            return true;
        }

        public void Stop(float fadeTime)
        {
            if (Room != null) Later++;
        }

        public void Tempo(ushort tempo, float time)
        {
            if (Room != null) Later++;
        }
    }

    // MatchPrebuild's worker
    internal void PrebuildStart(int width, int height)
    {
        var sw = Stopwatch.StartNew();
        System.IO.Directory.SetCurrentDirectory(_filesDir);
        Paths.UpdatePaths();
        Paths.ChooseMphPath();
        PickSkinGunAtStart();
        StartGearLoad(gun: true);
        StartSkinLoads();
        var (music, roomMusic) = (MusicPlayer.Host, Music.RoomHost);
        var held = new HeldRoomMusic();
        MusicPlayer.Host = null; // (silent, as the menu backdrop's build: the menus' music plays on)
        Music.RoomHost = held;
        try
        {
            if (!TryStartMatch(width, height) || _host == null) throw new System.InvalidOperationException(_error ?? "not a match");
        }
        finally
        {
            MusicPlayer.Host = music;
            Music.RoomHost = roomMusic;
        }
        // holiday mode's sky map and ground snow here too, as the menu backdrop's worker does (HolidaySnow.Prepare): built
        // in the match's first frame they held it 1.4 s after its first sounds (Shooting Gallery, Odin vc 748: the owner's
        // "hitched and the sound skipped"); cosmetic and built once per room, so the first frame finds it done
        long beforeSnow = sw.ElapsedMilliseconds;
        MphRecomp.Holiday.HolidaySnow.Prepare(_host!.Scene); // (TryStartMatch above threw without a host)
        long snowMs = sw.ElapsedMilliseconds - beforeSnow;
        _prebuiltMusic = held.Room;
        if (held.Later > 0) Log.Warn("MPHCampaign", $"lead-in build: {held.Later} room music calls after the room's (not made again)");
        _prebuilt = true;
        Log.Info("MPHCampaign", $"started {_room} during the lead-in in {sw.ElapsedMilliseconds} ms ({width}x{height}): {CampaignHost.MatchLoadTimes}, holiday snow {snowMs} ms");
    }

    // OnSurfaceChanged (GL thread), for a match PrebuildStart built
    void FinishPrebuilt(int width, int height)
    {
        _prebuilt = false;
        try
        {
            var sw = Stopwatch.StartNew();
            System.IO.Directory.SetCurrentDirectory(_filesDir);
            InitAudio();
            if (_prebuiltMusic is (int room, int track)) Music.PlayRoomMusic(room, track);
            _prebuiltMusic = null;
            if (_host != null && (_host.Scene.Size.X != width || _host.Scene.Size.Y != height)) _host.Scene.Size = new Vector2i(width, height);
            FinishGearLoad();
            FinishSkinLoads();
            FinishArenaHdNow(); // an Echoes arena opens finished, no stand-in first (CampaignArena.cs)
            Log.Info("MPHCampaign", $"lead-in match taken: audio, gear, skins and arena finished in {sw.ElapsedMilliseconds} ms ({width}x{height})");
        }
        catch (System.Exception ex)
        {
            _error = $"start failed: {ex.GetType().Name}: {ex.Message}";
            Log.Error("MPHCampaign", ex.ToString());
            StatusSink?.Invoke(_error);
        }
    }
}
