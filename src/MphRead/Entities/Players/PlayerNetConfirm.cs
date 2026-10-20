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
    public partial class PlayerEntity
    {
        public static int NetFormGraceFrames { get; set; } = 45;
        public const int NetFormHoldFrames = 30;
        public static Action<PlayerEntity, bool>? NetFormForced { get; set; }
        private long _netFormSwitchFrame = Int64.MinValue / 2;
        private long _netFormMismatchSince = -1;

        public static readonly HashSet<ItemInstanceEntity> NetConfirmedPickups = new(ReferenceEqualityComparer.Instance);

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
