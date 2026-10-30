using System;
using System.Collections.Generic;

namespace MphRead.Entities
{
    // recomp (match client, 2026-10-19 online reports): the local player waits for the host before two things it used
    // to do on its own. Only ever used with NetClient on, so a local match (and every replay) is unchanged.
    //   Forms: a form the host's snapshots disagree with is taken from the host only once this device's own last switch has
    //   had a round trip to reach it (NetFormGraceFrames, set from the ping) and the disagreement has lasted NetFormHoldFrames;
    //   it used to be 11 snapshots, so a lag spike after an unmorph put the player back in the ball ("I morphed without
    //   pressing morph"). Each one is reported (NetFormForced) for the device log.
    //   Pickups: an item is taken here only once the host took it for this player (ItemSync puts it in NetConfirmedPickups);
    //   taken here first, a pickup the host's copy of the player was out of range for played its sound and hid the item
    //   while the host kept it and took the gain back ("crates don't have pickups", "pickups differ between screens").
    //   Weapons: the host's snapshot showing a weapon it just took for this player reaches NetApply before the confirmed
    //   pickup runs here, so that pickup saw the weapon as held already: no charge for it, no third slot, the ammo sound
    //   (0.3.7 owner report: "charge beam didnt work for several beams"). A weapon the snapshots turn on gets its charge
    //   there, as every weapon pickup gives it, and for a second still counts as new to this player's confirmed pickup.
    public partial class PlayerEntity
    {
        public static int NetFormGraceFrames { get; set; } = 45;
        public const int NetFormHoldFrames = 30;
        public static Action<PlayerEntity, bool>? NetFormForced { get; set; }
        private long _netFormSwitchFrame = Int64.MinValue / 2;
        private long _netFormMismatchSince = -1;

        public static readonly HashSet<ItemInstanceEntity> NetConfirmedPickups = new(ReferenceEqualityComparer.Instance);

        private int _netNewWeapons;
        private long _netNewWeaponsFrame;

        // NetApply: the weapons the host's snapshot has and this player didn't (any seat; every weapon pickup charges)
        private void NetWeaponsGained(int gained)
        {
            for (int i = 0; i < 14; i++)
            {
                if ((gained & (1 << i)) != 0 && !HostFirstHunt) // (First Hunt: the charge is a second pickup, FirstHuntPickUp)
                {
                    _availableCharges[i] = true;
                }
            }
            if (gained != 0 && NetClient && this == Main)
            {
                _netNewWeapons |= gained;
                _netNewWeaponsFrame = (long)_scene.FrameCount;
            }
        }

        // the weapon pickup, a confirmed one: the weapon came with the snapshot just before it (taken as new once)
        private bool NetNewWeapon(BeamType weapon)
        {
            int bit = 1 << (int)weapon;
            if (!NetClient || (_netNewWeapons & bit) == 0)
            {
                return false;
            }
            _netNewWeapons &= ~bit;
            return (long)_scene.FrameCount - _netNewWeaponsFrame < 60;
        }

        // TrySwitchForms, a switch made: the local seat's grace starts again (its own press, or a forced one)
        private void NetFormSwitched()
        {
            if (NetClient && this == Main) _netFormSwitchFrame = (long)_scene.FrameCount;
        }

        // NetApply, the local seat in another form than the host's: whether to take the host's now
        private bool NetFormCorrectionDue()
        {
            long now = (long)_scene.FrameCount;
            if (_netFormMismatchSince < 0) _netFormMismatchSince = now;
            return now - _netFormSwitchFrame >= NetFormGraceFrames && now - _netFormMismatchSince >= NetFormHoldFrames;
        }
    }
}
