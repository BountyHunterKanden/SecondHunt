using MphRead;
using MphRead.Entities;

namespace MphRecomp.App;

// The MISSILES / POWER button (R1 by default) on a pickup beam (owner 2026-10-15): it goes back to whichever of the two
// the player had on before the pickup beams, then toggles between them as usual. Missiles -> Imperialist -> R1 =
// missiles; Power Beam -> Volt Driver -> Judicator -> R1 = Power Beam. With no missile ammo left it takes the Power Beam.
internal sealed partial class CampaignRenderer
{
    PlayerEntity? _baseWeaponPlayer;
    BeamType _baseWeapon = BeamType.PowerBeam;

    // every input frame: the last of Power Beam / missiles the local player had on (however it was picked)
    void NoteBaseWeapon(PlayerEntity p)
    {
        if (p != _baseWeaponPlayer)
        {
            _baseWeaponPlayer = p;
            _baseWeapon = BeamType.PowerBeam;
        }
        if (p.CurrentWeapon == BeamType.PowerBeam || p.CurrentWeapon == BeamType.Missile)
        {
            _baseWeapon = p.CurrentWeapon;
        }
    }

    BeamType MissileToggleWeapon(PlayerEntity p)
    {
        if (p.CurrentWeapon == BeamType.Missile)
        {
            return BeamType.PowerBeam;
        }
        if (p.CurrentWeapon == BeamType.PowerBeam)
        {
            return BeamType.Missile;
        }
        // on a pickup beam
        return _baseWeapon == BeamType.Missile && p.CanEquipWeapon(BeamType.Missile) ? BeamType.Missile : BeamType.PowerBeam;
    }
}
