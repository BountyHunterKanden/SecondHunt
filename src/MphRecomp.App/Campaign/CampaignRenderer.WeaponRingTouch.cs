using System;
using System.Threading;

namespace MphRecomp.App;

// Tapping a weapon on the weapon ring while it's open (owner 2026-10-18: "If touch controls are on can we make the weapons
// in the control pad weapon ring touchable", in AIM ONLY too). The ring sits in the middle of the dialogs' centred 4:3 box
// (MphRead PlayerWeaponRing.cs: slots 60 DS pixels out from 128, 96), so a tap's direction from there names the slot the
// same way the right stick does (CampaignHost.WheelSector). The tap picks it like the stick, and letting go of the ring's
// button equips it; a tap in the middle drops the pick (the Omega Cannon, when it shows there). Caller: MatchTouchOverlay.Down.
internal sealed partial class CampaignRenderer
{
    const int NoRingTap = -2; // nothing tapped; -1 = the middle (no pick)
    int _ringTap = NoRingTap; // UI thread -> the next sim step
    volatile bool _ringOpen; // BuildInput: the ring is up this step

    // UI thread: a tap at (x, y) in view pixels. True when the ring is open and the tap landed on it (the tap is used up).
    public bool RingTouch(float x, float y)
    {
        int width = _width, height = _height;
        if (!_ringOpen || width <= 0 || height <= 0)
        {
            return false;
        }
        float box = MphRead.Scene.HostHudDialogBox ? MathF.Min(width, height * 4 / 3f) : width;
        float dx = (x - (width - box) / 2) / box * 256 - 128;
        float dy = 96 - y / height * 192; // up = +y, as the stick
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance > 92)
        {
            return false; // well outside the ring: the tap aims as usual
        }
        int sector = distance < 28 ? -1 : MphRecomp.Campaign.CampaignHost.WheelSector(new System.Numerics.Vector2(dx, dy));
        Volatile.Write(ref _ringTap, sector);
        return true;
    }

    // BuildInput: the slot tapped since the last step, once
    int TakeRingTap() => Interlocked.Exchange(ref _ringTap, NoRingTap);
}
