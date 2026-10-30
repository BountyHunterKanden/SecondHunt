namespace MphRead.Entities
{
    // recomp: bots and the Hyper Beam (owner 2026-10-12, docs/DARK_SAMUS_HYPER.md H2; Metadata/HyperBeam.cs). The beam has no
    // touch button, so a bot equips it directly (like the Pirate Blaster and the Paralyzer). Index 12 = its BeamType in the
    // AI's weapon choice, affinity seek and switch candidates (PlayerAi.cs, recomp lines).
    // - Anyone else: presses (a bolt each), or a full charge and release for the 40-damage shot when the AI chose to charge.
    // - Dark Samus (her affinity row): holds fire through the charge into Hyper Mode rapid fire while she has a player
    //   target, and lets go when she loses it (PlayerHyperBeam.UpdateHyperBeamRapid fires; PlayerInput ends it on low UA).
    public partial class PlayerEntity
    {
        public partial class PlayerAiData
        {
            // Func2143A40's fire branch for the Hyper Beam (its own copy of that function's SetRandomDelay)
            private void HyperBeamFire(EquipInfo equip, WeaponInfo weapon, int shotDelay)
            {
                void SetRandomDelay()
                {
                    _shotDelay = weapon.ShotCooldown * 2 + (int)Rng.GetRandomInt2(shotDelay); // todo: FPS stuff
                }

                if (_player.CurrentWeapon != BeamType.HyperBeam)
                {
                    _player.TryEquipWeapon(BeamType.HyperBeam);
                }
                else if (weapon == HyperBeam.WeaponMPAffinity && CanChargeWeapon()
                    && (_targetPlayer != null && Flags2.TestFlag(AiFlags2.TargetPlayer) || equip.ChargeLevel > 0))
                {
                    if (_targetPlayer == null || !Flags2.TestFlag(AiFlags2.TargetPlayer))
                    {
                        SetRandomDelay(); // target lost: let go (no charged shot on her row)
                    }
                    else if (_buttons.R.FramesDown > 0 || _buttons.R.FramesUp > _shotDelay)
                    {
                        _buttons.R.IsDown = true; // charge, then rapid fire for as long as it's held
                    }
                }
                else if (Flags4.TestFlag(AiFlags4.Bit1) && CanChargeWeapon())
                {
                    if (!_player.Flags2.TestFlag(PlayerFlags2.Shooting) && _buttons.R.FramesUp <= _shotDelay
                        || equip.ChargeLevel >= weapon.FullCharge * 2) // todo: FPS stuff
                    {
                        SetRandomDelay(); // release: the charged shot
                    }
                    else
                    {
                        _buttons.R.IsDown = true;
                    }
                }
                else if (_buttons.R.FramesUp > _shotDelay)
                {
                    _buttons.R.IsDown = true;
                    SetRandomDelay();
                }
            }
        }
    }
}
