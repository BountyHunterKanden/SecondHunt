using OpenTK.Mathematics;

namespace MphRecomp.App;

internal sealed partial class CampaignRenderer
{
    // dev startat x,y,z[,yawDeg]: a second after the room starts, the player is moved there (to show a spot in a room,
    // e.g. the ROM collision survey's pass-through faces); a public build never honours it
    Vector3? _devStartAt;
    Vector3 _devStartFacing = -Vector3.UnitZ;
    int _devStartAtSteps;

    void ConfigureDevStartAt(LaunchArgs args)
    {
        if (MphRecomp.Config.RecompSettings.PublicBuild || args.Get("startat")?.Split(',') is not { Length: >= 3 } s) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        float[] v = s.Select(x => float.Parse(x, inv)).ToArray();
        _devStartAt = new Vector3(v[0], v[1], v[2]);
        if (v.Length > 3)
        {
            float yaw = MathHelper.DegreesToRadians(v[3]);
            _devStartFacing = new Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        }
    }

    void DevStartAtStep()
    {
        if (_devStartAt is not Vector3 at || _host?.Player is not MphRead.Entities.PlayerEntity p || ++_devStartAtSteps < 60) return;
        _devStartAt = null;
        p.Teleport(at, _devStartFacing, _host.Scene.GetNodeRefContaining(at));
        p.PrevPosition = at;
        Log.Info("MPHCampaign", $"dev startat: moved to {at}");
    }
}
