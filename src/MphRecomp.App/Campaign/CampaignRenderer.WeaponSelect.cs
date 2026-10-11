using MphRead;

namespace MphRecomp.App;

// The Omega Cannon's way back (release test T4 N15, owner 2026-10-10: "How does Omega cannon get selected again when a
// player grabs the pickup and then changes to another beam ... no way to do this in second hunt"). Vanilla keeps the
// Omega Cannon in the third weapon slot while you use the Power Beam or missiles, and the touch screen's third icon
// brings it back (MphRead PlayerInput: the affinity slot). Here: the weapon ring shows it in its middle (MphRead
// PlayerWeaponRing.DrawRingOmega, Core WeaponRingInput.CentrePick), the D-pad cycles only Power Beam / missiles / Omega
// while it's held (KeyboardMouseInput.CycleWeapon), and the touch controls get an OMEGA button (MatchTouchOverlay) while
// it's held but not in hand. Each sends the Omega Cannon's own weapon key, as vanilla's keys did; the sim is unchanged.
internal sealed partial class CampaignRenderer
{
    volatile bool _touchOmega;

    // UI thread, the touch overlay: OMEGA shows while the Omega Cannon sits in the third slot and another weapon is in
    // hand. A frame stale at worst
    public bool TouchOmega => _host?.Player is { } p && p.HostRingOmegaShown && p.CurrentWeapon != BeamType.OmegaCannon
        && !p.IsAltForm;

    // UI thread: OMEGA pressed; BuildInput sends the Omega Cannon's key on its next sim step
    public void TouchOmegaPressed() => _touchOmega = true;

    // BuildInput, once per sim step: a touch OMEGA press waiting
    bool TakeTouchOmega()
    {
        if (!_touchOmega) return false;
        _touchOmega = false;
        return true;
    }
}
