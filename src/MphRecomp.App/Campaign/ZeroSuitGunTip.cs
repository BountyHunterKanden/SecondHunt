using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Brawl Zero Suit (Core Campaign/BrawlZeroSuitRig.cs), owner 2026-10-15 18:0x: her third-person muzzle flash and charge glow
// on the Paralyzer's muzzle, not MPH's muzzle offset on Samus's shoot node (a gun's length past the barrel). Draw only:
// PlayerDraw's HostFxShootOffset, as the native bodies' cannon tips use it (MatchArmCannon.CannonFx3P clears it every frame,
// then calls this).
internal sealed partial class CampaignRetroBody
{
    // her held gun's muzzle where `p` stands now, as DrawBody poses and places her
    public bool HeldGunTip(PlayerEntity p, out Vector3 tip)
    {
        tip = default;
        if (Clips.AfterPose?.Target is not BrawlZeroSuitRig rig || !_anims.TryGetValue(p, out NativeBodyAnimator? a) || !a.Pose(p)) return false;
        if (!rig.GunTip(Body, out Vector3 local)) return false;
        Vector3 facing = a.WallFacing ?? new Vector3(p.Field70, 0, p.Field74);
        tip = Vector3.TransformPosition(local, Body.ModelMatrix(p.Hunter, p.Position, facing, Clips.Lift));
        return true;
    }

    // her first-person gun arm, cut like the Troopers' and Dark Samus's cannons (Core BrawlZeroSuitRig.Cannon); AppendCannon
    BodyArmCannon? HeldGunCannon() => Clips.AfterPose?.Target is BrawlZeroSuitRig rig ? rig.Cannon(Body) : null;
}

internal sealed partial class CampaignRenderer
{
    // the seat's flash on her gun's muzzle (left, up, forward of its position by its facing); false: not a held-gun body
    bool HeldGunFx3P(PlayerEntity p)
    {
        if (BodyOf(p) is not CampaignRetroBody body || !body.HeldGunTip(p, out Vector3 tip)) return false;
        var ahead = new Vector3(p.Field70, 0, p.Field74);
        if (ahead.LengthSquared < 1e-12f) return false;
        ahead.Normalize();
        Vector3 d = tip - p.Position;
        p.HostFxShootOffset = new Vector3(Vector3.Dot(d, Vector3.Cross(Vector3.UnitY, ahead)), d.Y, Vector3.Dot(d, ahead));
        return true;
    }

    // her shots drawn from the gun's muzzle like the Troopers' from the cannon (owner 10-15 18:1x: "her actual line of fire
    // doesnt look like it comes from the gun can we do the same type of fix for this that we did with trooper?";
    // ArmCannonBeamPath.DrawShotFromArm, from HostFxShootOffset = HeldGunFx3P's muzzle)
    bool HasHeldGun(PlayerEntity p) => BodyOf(p) is CampaignRetroBody body && body.Clips.AfterPose?.Target is BrawlZeroSuitRig;

    // dev solo run (SoloArena.cs), --es paralyzer 1: every seat handed the Paralyzer at the start, as MPH_DEV_PARALYZER does
    // on the PC (CampaignRenderer.ParalyzerFx.cs DevParalyzerHands; owner 10-15: "look at the paralyzer beam we have")
    // --es parafire 1 (dev): her trigger held 1.5 s, then tapped, over and over, as MPH_DEV_PARALYZER_FIRE on the PC
    bool _devParalyzerArg, _devParalyzerFire;
    void ConfigureZeroSuitDev(LaunchArgs args)
    {
        _devParalyzerArg = args.Get("paralyzer") == "1";
        _devParalyzerFire = args.Get("parafire") == "1";
    }

    // the main player's first-person gun (a body's cannon: her gun arm) as seen: its muzzle and barrel (owner 10-15: "The
    // muzzle flash and the charge beam don't look like they're at the end of the gun in first person"). The cannon rides
    // MPH's gun motion (the recoil, the charge kick) and is drawn with the gun's own projection (GunProjection), so MPH's
    // muzzle point isn't on it; this is the world point the scene's projection shows where the drawn muzzle is, at its depth
    bool FirstPersonGunTip(PlayerEntity p, out Vector3 tip, out Vector3 barrel)
    {
        tip = barrel = default;
        if (_host == null || p != _host.Player || p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson) || !_cannonActive
            || _cannonBody == null || BodyOf(p) != _cannonBody)
        {
            return false;
        }
        Scene scene = _host.Scene;
        Matrix4 view = scene.ViewMatrix, proj = scene.PerspectiveMatrix;
        Vector4 v = new Vector4(_cannonModel.Row3.Xyz, 1) * view;
        Vector4 g = v * GunProjection(scene), c = v * proj;
        if (g.W <= 1e-5f || c.W <= 1e-5f) return false;
        c.X = g.X / g.W * c.W;
        c.Y = g.Y / g.W * c.W;
        Vector4 seen = c * Matrix4.Invert(proj);
        if (MathF.Abs(seen.W) <= 1e-6f) return false;
        tip = (new Vector4(seen.Xyz / seen.W, 1) * Matrix4.Invert(view)).Xyz;
        barrel = _cannonModel.Row2.Xyz;
        if (barrel.LengthSquared < 1e-12f) return false;
        barrel.Normalize();
        return true;
    }

    // the main player wears a Zero Suit: no helmet, so no helmet / visor frame round the screen (owner 10-15 18:0x: "we
    // have to entirely get rid of those visor/helmet corners and top strip"; PlayerHud HostNoHelmet)
    bool MainHasNoHelmet()
    {
        int seat = _host?.Player.SlotIndex ?? -1;
        return seat >= 0 && seat < _seatSkin.Length && _seatSkin[seat]?.Contains("ZeroSuit", System.StringComparison.OrdinalIgnoreCase) == true;
    }
}
