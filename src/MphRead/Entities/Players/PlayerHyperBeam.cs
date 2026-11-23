using System;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the Hyper Beam on the player (Metadata/HyperBeam.cs; owner 2026-10-12, docs/DARK_SAMUS_HYPER.md H2), fired as
    // Corruption's gun fires it (main.dol, 11-13; owner: "it's a new beam just like pirate blaster"). A press is one bolt
    // (PlayerInput, as any beam). A charge released from MinCharge on fires the stream's first shot there (TryFireWeapon,
    // which pays for the whole stream), then UpdateHyperBeamStream fires one more every StreamGap for max(0.1, charge) x
    // StreamTime, whether fire is held or not, as Corruption's release. The stream shots reset the time since a shot, so
    // presses wait; the charge can't build while it runs (PlayerInput: no hum and no release, HyperBeamRapidCharge).
    // Sim only (replays and network matches run it the same everywhere: a shot event's release starts the stream on every
    // copy, as the blaster's burst).
    public partial class PlayerEntity
    {
        // recomp: the app draws Hyper Beam shots, muzzle flash and charge in Corruption's look (the user's own Corruption
        // import, MphRecomp.App CampaignRenderer.HyperBeamFx.cs): MphRead's recoloured Power Beam isn't drawn
        // (BeamProjectileEntity.HostHyperBeamLook -> HostOwnLook). Draw only
        public static bool HostOwnHyperBeamLook { get; set; }
        // recomp: the app plays the Hyper Beam's sounds in Corruption's: MphRead's stand-in Power Beam shot and charge
        // (PlayerSound) and hit (BeamProjectileEntity.HostOwnSfx) aren't played. Each press's bolt and each release is
        // counted instead (HostHyperBeamShots; a release is "charged" and its sound covers its stream), for the app to
        // sound at the cannon
        public static bool HostOwnHyperBeamSfx { get; set; }
        public static bool HostOwnHyperBeamHitSfx { get; set; }
        public int HostHyperBeamShots { get; private set; }
        public bool HostHyperBeamLastCharged { get; private set; }

        // a charged release's stream is running (the app's burst muzzle and charge glow; bots; tests)
        public bool HyperBeamStreaming => _hyperStreamLength > 0;
        public int HostHyperBeamStreamShots { get; private set; } // the release and the shots after it (tests)

        // the stream: frames since the release, its length in frames (0 = none), and whether it came from Dark Samus's
        // affinity row (its shots are HyperBeam.StreamMPAffinity's)
        private int _hyperStreamAge = 0;
        private int _hyperStreamLength = 0;
        private bool _hyperStreamAffinity = false;

        // the curl's seeds (BeamHyperWind.cs): the life the releases were counted in (the seat's deaths then), the releases
        // in it, the last release's seed and its stream's (taken from it at the release)
        private int _hyperReleaseLife = -1;
        private int _hyperReleases = 0;
        private uint _hyperReleaseSeed = 0;
        private uint _hyperStreamSeed = 0;

        // BeamProjectileEntity.Spawn, a charged Hyper Beam shot: a stream shot's seed comes from its release's, a release's
        // from the seat, its life and its count in that life (a copy that joined mid-life is off only till the seat dies)
        public uint HyperWindShotSeed(Vector3 position)
        {
            if (HyperBeamStreaming)
            {
                return BeamProjectileEntity.HyperStreamSeed(_hyperStreamSeed, HostHyperBeamStreamShots);
            }
            int life = SlotIndex >= 0 && SlotIndex < GameState.Deaths.Length ? GameState.Deaths[SlotIndex] : 0;
            if (life != _hyperReleaseLife)
            {
                _hyperReleaseLife = life;
                _hyperReleases = 0;
            }
            _hyperReleaseSeed = BeamProjectileEntity.HyperReleaseSeed(SlotIndex, life, _hyperReleases);
            return _hyperReleaseSeed;
        }

        // PlayerInput and PlayerSideView: no charge hum and no charged release while a stream runs
        private bool HyperBeamRapidCharge => HyperBeamStreaming;

        // a weapon whose shots, muzzle flash and charge the app draws itself (the Paralyzer, the Hyper Beam)
        private static bool HostOwnBeamLook(BeamType beam)
        {
            return beam == BeamType.Paralyzer && HostOwnParalyzerLook || beam == BeamType.HyperBeam && HostOwnHyperBeamLook;
        }

        // TryFireWeapon (through OnPirateBlasterShot): a shot went out at the charge it was released at
        private void OnHyperBeamShot(WeaponInfo weapon)
        {
            if (weapon.Beam != BeamType.HyperBeam || EquipInfo.ChargeLevel < weapon.MinCharge * 2) // todo: FPS stuff
            {
                return;
            }
            float charge = Math.Clamp(EquipInfo.ChargeLevel / (float)(weapon.FullCharge * 2), 0.1f, 1f); // todo: FPS stuff
            _hyperStreamLength = (int)(charge * HyperBeam.StreamTime * 2); // todo: FPS stuff
            _hyperStreamAge = 0;
            _hyperStreamAffinity = weapon == HyperBeam.WeaponMPAffinity;
            HostHyperBeamStreamShots = 1;
            _hyperStreamSeed = _hyperReleaseSeed;
            _hyperReleases++;
        }

        // every frame after ProcessInput; _timeSinceShot counts from the stream's last shot (0 on the frame it went out)
        private void UpdateHyperBeamStream()
        {
            if (_hyperStreamLength == 0)
            {
                return;
            }
            if (_hyperStreamAge >= _hyperStreamLength || _health == 0 || IsAltForm || AttachedEnemy != null || ShieldWallStaggered
                || CurrentWeapon != BeamType.HyperBeam || EquipInfo.Weapon.Beam != BeamType.HyperBeam)
            {
                _hyperStreamLength = 0;
                _hyperStreamAge = 0;
                return;
            }
            _hyperStreamAge++;
            EquipInfo.ChargeLevel = 0;
            _paralyzerPressPending = false; // a press during the stream doesn't fire when it ends
            if (_timeSinceShot < HyperBeam.StreamGap * 2) // todo: FPS stuff
            {
                return;
            }
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
            WeaponInfo stream = _hyperStreamAffinity ? HyperBeam.StreamMPAffinity : HyperBeam.StreamMP;
            EquipInfo.Weapon = stream;
            EquipInfo.ChargeLevel = (ushort)(stream.FullCharge * 2); // a charged shot (the burst), costing nothing
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
            EquipInfo.ChargeLevel = 0;
            if (result == BeamResultFlags.NoSpawn)
            {
                _hyperStreamLength = 0;
                _hyperStreamAge = 0;
                return;
            }
            _timeSinceShot = 0;
            HostHyperBeamStreamShots++;
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
            // (no shot sound: the release's, Corruption's charged fire, runs through the stream)
        }
    }
}
