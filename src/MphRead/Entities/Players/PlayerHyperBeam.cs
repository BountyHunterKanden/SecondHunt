using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the Hyper Beam on the player (Metadata/HyperBeam.cs; owner 2026-10-12, docs/DARK_SAMUS_HYPER.md H2).
    // Everyone fires it like any charging beam: a bolt a press, a full charge for the 40-damage shot. With Dark Samus's
    // affinity row (HyperBeam.WeaponMPAffinity) a full charge held is Hyper Mode rapid fire instead: UpdateHyperBeamRapid
    // fires HyperBeam.RapidMP every RapidGap frames from the frame the charge fills, while fire is held and the UA lasts
    // (PlayerInput releases the charge when the UA drops below one shot). Releasing ends it with no charged shot.
    // Sim only (replays and network matches run it the same everywhere).
    public partial class PlayerEntity
    {
        // recomp: the app draws Hyper Beam shots, muzzle flash and charge in Corruption's look (the user's own Corruption
        // import, MphRecomp.App CampaignRenderer.HyperBeamFx.cs): MphRead's recoloured Power Beam isn't drawn
        // (BeamProjectileEntity.HostHyperBeamLook -> HostOwnLook). Draw only
        public static bool HostOwnHyperBeamLook { get; set; }
        // recomp: the app plays the Hyper Beam's sounds in Corruption's: MphRead's stand-in Power Beam shot and charge
        // (PlayerSound) and hit (BeamProjectileEntity.HostOwnSfx) aren't played. Each shot is counted instead
        // (HostHyperBeamShots, whether it was charged or a rapid-fire shot), for the app to sound at the cannon
        public static bool HostOwnHyperBeamSfx { get; set; }
        public static bool HostOwnHyperBeamHitSfx { get; set; }
        public int HostHyperBeamShots { get; private set; }
        public bool HostHyperBeamLastCharged { get; private set; }
        public bool HostHyperBeamLastRapid { get; private set; }

        // Hyper Mode rapid fire is on this frame (the app's Hyper Mode glow and sounds; bots; tests)
        public bool HyperBeamRapid { get; private set; }
        public int HostHyperBeamRapidShots { get; private set; } // (tests)

        // frames to the next rapid-fire shot (0 = the next one goes out this frame)
        private int _hyperRapidTimer = 0;

        // her affinity row at full charge: held fire is rapid fire (PlayerInput: no charge hum or charged shot)
        private bool HyperBeamRapidCharge => EquipInfo.Weapon == HyperBeam.WeaponMPAffinity
            && EquipInfo.ChargeLevel >= EquipInfo.Weapon.FullCharge * 2; // todo: FPS stuff

        // a weapon whose shots, muzzle flash and charge the app draws itself (the Paralyzer, the Hyper Beam)
        private static bool HostOwnBeamLook(BeamType beam)
        {
            return beam == BeamType.Paralyzer && HostOwnParalyzerLook || beam == BeamType.HyperBeam && HostOwnHyperBeamLook;
        }

        // every frame after ProcessInput
        private void UpdateHyperBeamRapid()
        {
            if (CurrentWeapon != BeamType.HyperBeam || !HyperBeamRapidCharge || !Flags2.TestFlag(PlayerFlags2.Shooting)
                || _health == 0 || IsAltForm || AttachedEnemy != null || ShieldWallStaggered)
            {
                HyperBeamRapid = false;
                _hyperRapidTimer = 0;
                return;
            }
            HyperBeamRapid = true;
            if (_hyperRapidTimer > 0)
            {
                _hyperRapidTimer--;
                return;
            }
            _hyperRapidTimer = HyperBeam.RapidGap * 2 - 1; // todo: FPS stuff
            if (!Flags2.TestFlag(PlayerFlags2.Cloaking))
            {
                _cloakTimer = 0;
            }
            Vector3 shotVec = _aimPosition - _muzzlePos;
            if (_disruptedTimer > 0)
            {
                shotVec.X += Fixed.ToFloat((int)Rng.GetRandomInt2(24576) - 12288);
                shotVec.Y += Fixed.ToFloat((int)Rng.GetRandomInt2(24576) - 12288);
                shotVec.Z += Fixed.ToFloat((int)Rng.GetRandomInt2(24576) - 12288);
            }
            shotVec = shotVec.Normalized();
            WeaponInfo curWeapon = EquipInfo.Weapon;
            var chargeLevel = EquipInfo.ChargeLevel;
            EquipInfo.Weapon = HyperBeam.RapidMP;
            EquipInfo.ChargeLevel = 0;
            BeamSpawnFlags flags = BeamSpawnFlags.NoMuzzle;
            if (_doubleDmgTimer > 0)
            {
                flags |= BeamSpawnFlags.DoubleDamage;
            }
            else if (IsPrimeHunter)
            {
                flags |= BeamSpawnFlags.PrimeHunter;
            }
            BeamResultFlags result = BeamProjectileEntity.Spawn(this, EquipInfo, _muzzlePos, shotVec, flags, NodeRef, _scene);
            EquipInfo.Weapon = curWeapon;
            EquipInfo.ChargeLevel = chargeLevel;
            if (result == BeamResultFlags.NoSpawn)
            {
                return; // out of UA (PlayerInput releases the charge)
            }
            _timeSinceShot = 0;
            HostHyperBeamRapidShots++;
            if (HudViewer)
            {
                HudOnFiredShot();
            }
            if (_muzzleEffect != null)
            {
                _scene.UnlinkEffectEntry(_muzzleEffect);
                _muzzleEffect = null;
            }
            int effectId = Metadata.MuzzleEffectIds[PirateBlaster.BaseIndex(CurrentWeapon)];
            _muzzleEffect = _scene.SpawnEffectGetEntry(effectId, _gunVec2, _gunVec1, FxMuzzlePos);
            if (_muzzleEffect != null && (!IsMainPlayer || HostOwnHyperBeamLook))
            {
                _muzzleEffect.SetDrawEnabled(false);
            }
            PlayBeamShotSfx(BeamType.HyperBeam, charged: false, continuous: false, homing: false, amountA: 0);
        }
    }
}
