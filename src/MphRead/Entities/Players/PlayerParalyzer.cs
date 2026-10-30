using System;

namespace MphRead.Entities
{
    // recomp: the Paralyzer on the player (Metadata/Paralyzer.cs; owner 2026-10-12, docs/ZERO_SUIT_SAMUS.md section 7).
    // Zero Suit's charged Paralyzer shot STUNS: for Paralyzer.StunFrames the target can't move, jump, morph or unmorph
    // (and can't roll in its alt form), but it can still turn, aim and shoot. Gravity still pulls a target hit in the
    // air. After a stun ends the same player can't be stunned again for Paralyzer.ImmunityFrames, so her 1 s charge can't
    // chain a 1.5 s stun forever. Sim only (replays and network matches run it the same everywhere).
    // Also her lower health (no armour): HostHealthPercent.
    public partial class PlayerEntity
    {
        // recomp: the app draws Paralyzer shots, muzzle flash and charge in Brawl's look (the user's own Brawl import,
        // MphRecomp.App CampaignRenderer.ParalyzerFx.cs): MphRead's recoloured Power Beam isn't drawn (BeamProjectileEntity
        // .HostParalyzerLook -> HostOwnLook). Draw only
        public static bool HostOwnParalyzerLook { get; set; }
        // recomp: the app plays the Paralyzer's sounds in Brawl's (the user's own Brawl import, CampaignRenderer.ParalyzerFx.cs):
        // MphRead's stand-in Power Beam shot and charge (PlayerSound) and hit (BeamProjectileEntity.HostOwnSfx) aren't played.
        // Each shot is counted instead (HostParalyzerShots, whether it was charged), for the app to sound at the cannon
        public static bool HostOwnParalyzerSfx { get; set; }
        public static bool HostOwnParalyzerHitSfx { get; set; }
        public int HostParalyzerShots { get; private set; }
        public bool HostParalyzerLastCharged { get; private set; }

        // the app's health for this seat as a percent of every other hunter's (Zero Suit: Paralyzer.HealthPercent); 100 = vanilla.
        // Multiplayer only (the campaign's health comes from the save)
        public int HostHealthPercent { get; set; } = 100;

        // the energy tank a multiplayer seat counts with: spawns at a tank - 1 and tops out at two tanks - 1, like vanilla
        private int MpEnergyTank => HostHealthPercent == 100 ? Values.EnergyTank : Values.EnergyTank * HostHealthPercent / 100;

        private ushort _paralyzedTimer = 0;
        // frames since the last stun ended (capped): another stun needs Paralyzer.ImmunityFrames
        private ushort _timeSinceParalyzed = UInt16.MaxValue;

        public bool ParalyzerStunned => _paralyzedTimer > 0;
        // a stunning hit would land now (not stunned, immunity over): bots save their charge for this
        public bool ParalyzerStunnable => _paralyzedTimer == 0 && _timeSinceParalyzed >= Paralyzer.ImmunityFrames;
        public int HostParalyzedTimer => _paralyzedTimer; // (tests, network snapshots)

        // a Paralyzer press its cooldown swallowed: the bolt goes out as soon as the cooldown ends, while fire is still held
        // and before the charge, so every press fires once before charging like the other beams (owner 2026-10-15: "the
        // beam needs to fire one shot before charging like the rest of the beams"; one bolt a press, 1/3 s apart)
        private bool _paralyzerPressPending;

        // ProcessInput, every frame before the fire check: true = try that bolt now
        private bool ParalyzerPendingShot()
        {
            // (the Hyper Beam too: its 0.4 s cooldown swallowed presses the same way, PlayerHyperBeam.cs)
            if (CurrentWeapon != BeamType.Paralyzer && CurrentWeapon != BeamType.HyperBeam || !Flags2.TestFlag(PlayerFlags2.Shooting))

            {
                _paralyzerPressPending = false;
                return false;
            }
            if (Controls.Shoot.IsPressed)
            {
                _paralyzerPressPending = true;
            }
            return _paralyzerPressPending && EquipInfo.ChargeLevel < EquipWeapon.MinCharge * 2; // todo: FPS stuff
        }

        // TakeDamage: a hit landed (the player is still alive)
        private void OnParalyzerHit(BeamProjectileEntity beam)
        {
            if (!beam.HostParalyzerStun || _paralyzedTimer > 0 || _timeSinceParalyzed < Paralyzer.ImmunityFrames)
            {
                return;
            }
            _paralyzedTimer = Paralyzer.StunFrames;
            HostParalyzerStuns++;
            // stay where the shot landed: no sliding on (falling, if in the air, carries on)
            Speed = Speed.WithX(0).WithZ(0);
        }

        // ProcessInput, every frame while alive (next to the frozen timer)
        private void UpdateParalyzerStun()
        {
            if (_paralyzedTimer > 0)
            {
                _paralyzedTimer--;
                _timeSinceParalyzed = 0;
            }
            else if (_timeSinceParalyzed < UInt16.MaxValue)
            {
                _timeSinceParalyzed++;
            }
        }

        // spawn and death
        private void ResetParalyzerStun()
        {
            _paralyzedTimer = 0;
            _timeSinceParalyzed = UInt16.MaxValue;
        }

        // network snapshots (MphRecomp Net/SeatAbilities): the host's timer wins. A puppet takes it as it arrives; the local
        // seat (running ahead of the host) keeps its own count while the two agree within a few frames, the way the
        // frozen timer is corrected (PlayerNet.cs)
        public void NetApplyParalyzed(int timer, bool exact)
        {
            timer = Math.Clamp(timer, 0, Paralyzer.StunFrames);
            if (!exact && ((timer > 0) == (_paralyzedTimer > 0) && Math.Abs(timer - _paralyzedTimer) <= 8
                || _paralyzedTimer == 0 && timer <= 8 && _timeSinceParalyzed <= 16)) // (the tail of a stun it already ended)
            {
                return;
            }
            if (timer > 0 && _paralyzedTimer == 0)
            {
                _timeSinceParalyzed = 0;
            }
            _paralyzedTimer = (ushort)timer;
        }

        // (tests) stuns landed on this player
        public int HostParalyzerStuns { get; set; }

        // (tests) a full UA pool
        public void HostRefillUa() => _ammo[0] = _ammoMax[0];
    }
}
