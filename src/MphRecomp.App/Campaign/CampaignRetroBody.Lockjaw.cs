using System.Collections.Generic;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Beyond Sylux's Prime 4 Lockjaw (Core BeyondLockjaw, bodies/MP4SyluxLockjaw): one clip clock per seat, stepped after each
// sim step while that seat is in its alt form, drawn on MphRead's alt transform (CampaignRenderer's MatchLockjaw.cs)
internal sealed partial class CampaignRetroBody
{
    readonly Dictionary<PlayerEntity, BeyondLockjaw.Animator> _lockjaws = new();

    public void TickLockjaw(PlayerEntity p)
    {
        if (!_lockjaws.TryGetValue(p, out BeyondLockjaw.Animator? a)) _lockjaws[p] = a = new BeyondLockjaw.Animator(Body, Clips);
        string was = a.Playing;
        a.Step(p.Position, p.HostAltTurn.Row2.Xyz, Dt);
        if (LogClips && a.Playing != was) Log.Info(Tag, $"body {Id}: seat {p.SlotIndex} lockjaw: {a.Playing}");
    }

    // its orb's place (BeyondLockjaw.Core) when the opaque pass drew it, else null; alpha as DrawBody's (CurAlpha: the death
    // fade, cloak)
    public Vector3? DrawLockjaw(PlayerEntity p, Matrix4 view, Matrix4 proj, SuitPass pass, bool flash, float alpha)
    {
        if (!_lockjaws.TryGetValue(p, out BeyondLockjaw.Animator? a)) return null;
        // its middle on the alt form's collision sphere's centre (Sylux's: his position), where vanilla Lockjaw draws
        Vector3 centre = p.Position;
        centre.Y += MphRead.Fixed.ToFloat(p.Values.AltColYPos);
        Matrix4 model = BeyondLockjaw.ModelMatrix(Body, centre, p.HostAltTurn);
        Draw(a.Clips, null, model, view, proj, pass, alpha, flash);
        return pass == SuitPass.Opaque && !Failed && alpha > 0 ? BeyondLockjaw.Core(Body, model) : null;
    }
}
