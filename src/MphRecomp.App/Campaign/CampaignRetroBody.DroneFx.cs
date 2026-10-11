using System.Collections.Generic;
using System.IO;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Import.Effects;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The Scout Drone's Prime 1 effects (CampaignRenderer.DroneFx.cs): the drone body's own fx folder and its bones posed as
// the body draws them. Kept in its own file beside the characters thread's CampaignRetroBody.cs.
internal sealed partial class CampaignRetroBody
{
    bool _fxRead;
    RetroParticles? _fxLib;
    BodyFx.Config? _fxCfg;
    readonly Dictionary<uint, (short[] Pcm, int Rate)> _fxSounds = new();

    // Prime's sounds the effects play (fx.cfg "sfx" lines: sfx_<id>.wav), by Prime sound id; empty without them
    public IReadOnlyDictionary<uint, (short[] Pcm, int Rate)> FxSounds
    {
        get
        {
            _ = Fx; // read with the effects
            return _fxSounds;
        }
    }

    public string FxDir => Path.Combine(Body.Dir, "fx");

    // bodies/<id>/fx/ (Core Import/Effects/BodyFx.cs), read on first use; null when the body was imported without it
    public (RetroParticles Lib, BodyFx.Config Cfg)? Fx
    {
        get
        {
            if (!_fxRead)
            {
                _fxRead = true;
                try
                {
                    _fxCfg = BodyFx.Config.Load(Body.Dir);
                    if (_fxCfg != null) _fxLib = RetroParticles.Load(FxDir);
                    foreach (uint id in _fxCfg?.Sfx.Keys ?? (IEnumerable<uint>)System.Array.Empty<uint>())
                    {
                        string wav = Path.Combine(FxDir, BodyFx.SfxFile(id));
                        if (File.Exists(wav) && MusyX.ReadWav(wav, out int rate) is short[] pcm && pcm.Length > 0) _fxSounds[id] = (pcm, rate);
                    }
                    if (_fxCfg != null) Log.Info(Tag, $"body {Id}: {_fxLib!.Count} Prime effects, {_fxCfg.Events.Count} clip events, {_fxSounds.Count} Prime sounds");
                }
                catch (System.Exception ex)
                {
                    Log.Warn(Tag, $"body {Id}: its effects failed to load, drawn without them: {ex.Message}");
                    _fxCfg = null;
                    _fxLib = null;
                    _fxSounds.Clear();
                }
            }
            return _fxCfg != null && _fxLib != null ? (_fxLib, _fxCfg) : null;
        }
    }

    // this seat's drone posed as it draws now: its model -> world matrix (Body.World then holds the bones), or null
    public Matrix4? PoseDrone(PlayerEntity p)
    {
        if (!_drones.TryGetValue(p, out DroneBody.Animator? d) || !d.Clips.Pose(null)) return null;
        return DroneBody.ModelMatrix(Body, p.DroneDrawCenter, p.DroneDrawFacing, p.FacingVector);
    }

    public float ClipSeconds(string clip) => Body.Clip(clip)?.Duration ?? 0;
}
