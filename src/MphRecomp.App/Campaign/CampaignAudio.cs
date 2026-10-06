using MphRead;
using MphRead.Sound;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Wires MphRead's Sfx/Music host seams (MusicPlayer.Host, Sfx.SetHost) to the shared Android backend in GameAudio.cs
// for the campaign. CampaignActivity.cs only calls the handful of methods below (InitAudio/ReassertSfxHost/
// UpdateAudioPause from CampaignRenderer's own OnSurfaceChanged/OnDrawFrame, PauseAudio/ResumeAudio/DisposeAudio
// from CampaignActivity's lifecycle methods) -- everything else lives here so the file another agent is editing for
// the gun/suit selector stays untouched apart from those call sites.
internal sealed partial class CampaignRenderer
{
    GameMusicPlayer? _music;
    GameSfxPlayer? _sfx;

    // set by CampaignActivity right after construction (RecompSettings.Music: plain / original / hq / hqtone -- see
    // GameAudio.cs's GameMusicPlayer.MusicMode)
    public string? SettingsPath;

    // Called once from OnSurfaceChanged, before the room/ship starts. Music.Host must already be set at that point:
    // the very first room's Music.TryPlayRoomMusic (SceneSetup.cs) runs synchronously inside CampaignHost.Start,
    // before this method's caller gets control back.
    void InitAudio()
    {
        if (_music != null)
        {
            return;
        }
        MphRecomp.Config.RecompSettings? settings = SettingsPath != null ? MphRecomp.Config.RecompSettings.Load(SettingsPath) : null;
        string? music = settings?.Music;
        _music = new GameMusicPlayer(music);
        MusicPlayer.Host = _music;
        _sfx = new GameSfxPlayer();
        _sfx.LoadTables();
        // OPTIONS > AUDIO's volumes (Frontend/AudioOptions.cs), as the DS applies its saved ones to the whole game
        float musicGain = GameMusicPlayer.MasterGain(settings?.MusicVolume ?? 9);
        _music.MasterVolume = musicGain;
        _sfx.SfxMasterVolume = GameMusicPlayer.MasterGain(settings?.SfxVolume ?? 9);
        _sfx.StreamMasterVolume = musicGain;
        Log.Info("MPHCampaign", $"audio host installed (music {music ?? "original"})");
        _sfx.ListenerPosition = () => _host?.Player.CameraInfo.Position ?? Vector3.Zero;
        _sfx.ListenerUp = () => _host?.Player.CameraInfo.TrueUp ?? Vector3.UnitY;
        _sfx.ListenerFacing = () => _host?.Player.CameraInfo.Facing ?? -Vector3.UnitZ;
    }

    // Re-asserted every frame: Sfx.Load (called from inside CampaignHost.Start and every room transition) always
    // resets Sfx.Instance back to the silent no-op base first -- see Sfx.SetHost's doc comment. Cheap (a reference
    // compare) when already installed, which is every frame but the one right after a room loads.
    void ReassertSfxHost()
    {
        if (_sfx != null && !ReferenceEquals(Sfx.Instance, _sfx))
        {
            Sfx.SetHost(_sfx);
        }
    }

    bool _audioPaused, _musicPaused;

    // Diagnostic for "sounds play seconds before what they belong to" (owner report 2026-09-30): OnDrawFrame runs the
    // sim steps (which start sounds at once) and THEN draws, so a frame whose draw stalls -- a new room's first draw
    // uploading textures/shaders on a cold load -- puts every sound started in it that long ahead of the picture.
    // Logs any gap between frame starts over 150 ms; read it next to the "sample N" lines' logcat timestamps.
    readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    double _lastFrameStart = -1;

    void TraceFrameGap()
    {
        double now = _frameClock.Elapsed.TotalSeconds;
        if (_lastFrameStart >= 0 && now - _lastFrameStart > 0.15)
        {
            Log.Info("MPHAudio", $"frame gap {(now - _lastFrameStart) * 1000:0} ms before sim frame " +
                $"{_host?.Frame} room {_host?.RoomId}: sounds started in the previous frame led its picture by this much");
        }
        _lastFrameStart = now;
    }

    // driven every frame from OnDrawFrame with (the gun-adjust pause || the pause menu is open) -- the same
    // condition that already freezes _host.Step
    void UpdateAudioPause(bool paused)
    {
        TraceFrameGap();
        // RECOMP SETTINGS > MUSIC from the pause menu switches live (one compare a frame; GameAudio.cs SetQuality): the
        // room's song carries on in the new mode once the music resumes
        if (PauseMenu?.IsOpen == true) _music?.SetQuality(PauseMenu.Settings.Music);
        // while the cursor is on that row the paused music plays, so a change is heard as it's made (owner 2026-10-05);
        // the sound effects stay paused
        bool musicPaused = paused && PauseMenu?.MusicRowFocused != true;
        if (musicPaused != _musicPaused)
        {
            _musicPaused = musicPaused;
            if (musicPaused)
            {
                _music?.Pause();
            }
            else if (_music != null)
            {
                _music.Play(_music.Volume);
            }
        }
        if (paused == _audioPaused)
        {
            return;
        }
        _audioPaused = paused;
        _sfx?.SetPaused(paused);
    }

    // Activity.OnPause: always pauses, regardless of the in-game pause state (the app is backgrounding)
    public void PauseAudio()
    {
        _music?.Pause();
        _sfx?.SetPaused(true);
    }

    // Activity.OnResume: only actually resumes if the in-game menu/gun-pause wasn't already holding it paused
    public void ResumeAudio()
    {
        if (!_musicPaused && _music != null)
        {
            _music.Play(_music.Volume);
        }
        if (!_audioPaused)
        {
            _sfx?.SetPaused(false);
        }
    }

    public void DisposeAudio()
    {
        _music?.Dispose();
        _sfx?.Dispose();
    }

    // ---------------------------------------------------------------- the ship screens' music (vanilla mode 11)

    // No CampaignHost steps under the briefing, the planet select or the ship movies, so nothing ran Music.UpdateMusic
    // there: a PlaySeq loaded and never started, and a fade never finished. CampaignShip.ShipInput calls this in the
    // host's place, only while the host isn't stepping (never twice a frame).
    void ShipMusicTick() => MphRead.Music.UpdateMusic();

    // the planet select's enter (rev 1 ov0 0x21054DC; sound census ov0-065..068): SEQ_SHIP unless it's already playing
    // (IsMusicStopped || GetCurrentSeq != 4). Both ways in get here with the music stopped: the new game's Story Intro
    // movie (after the briefing's StopMusic) and LAUNCH's take-off movie (the music stops as the fade starts).
    void PlanetSelectMusic()
    {
        if (MphRead.Music.CurrentSeq != SeqId.SHIP)
        {
            MphRead.Music.PlaySeq(SeqId.SHIP);
        }
    }

    // the briefing's load (0x2105F98; census ov0-077/078): SEQ_NEW_GAME, under the briefing's dry echo preset 1
    void BriefingMusic() => MphRead.Music.PlaySeq(SeqId.NEW_GAME);

    // the game's StopMusic(fade): leaving mode 11 at LAND (0x21043A8, fade 0; census ov0-048) and the briefing's end
    // (0x2105CF0, fade 5; ov0-074). Mode 11 runs the sound engine once per 30 Hz game frame (census a9-327).
    void StopShipMusic(int fadeFrames = 0) => MphRead.Music.Stop(fadeFrames / 30f);
}
