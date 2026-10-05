using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer.Net;

namespace MphRecomp.Campaign
{
    // The match host's side of remote play: a remote seat (MatchPlayer.Remote -> PlayerEntity.HostRemote) takes the
    // latest input its client sent. Call before Step; the input holds until the next one arrives (a missing frame
    // repeats the last buttons and view, as MphRead's own keyboard state would).
    public sealed partial class CampaignHost
    {
        public void SetRemoteInput(int seat, in NetInput input)
        {
            PlayerEntity p = PlayerEntity.Players[seat];
            if (!p.HostRemote)
            {
                return;
            }
            SetControls(p.Controls, input.MoveX, input.MoveY, input.Buttons, input.SelectWeapon);
            p.HostRemoteYaw = input.Yaw;
            p.HostRemotePitch = input.Pitch;
            // the client's view counts only once it has taken the game's latest re-aim (spawn/teleport)
            p.HostRemoteAimEpoch = (byte)p.NetAimEpoch == input.AimEpoch ? p.NetAimEpoch : -1;
        }

        // a remote player left: the game takes the seat out of the match (PlayerProcess: Disconnected, not Active)
        public void DisconnectSeat(int seat)
        {
            PlayerEntity p = PlayerEntity.Players[seat];
            p.LoadFlags &= ~LoadFlags.Connected;
            SetControls(p.Controls, 0, 0, CampaignButtons.None, BeamType.None);
        }

        // a match client whose host is gone: stop here (the results come up as for any ended match)
        public void EndMatchNow()
        {
            Ended = true;
        }

        // the same control mapping as Step (CampaignHost.cs) for any seat's controls
        internal static void SetControls(PlayerControls c, int moveX, int moveY, CampaignButtons b, BeamType select)
        {
            c.MoveUp.HostDown = moveY > 0;
            c.MoveDown.HostDown = moveY < 0;
            c.MoveRight.HostDown = moveX > 0;
            c.MoveLeft.HostDown = moveX < 0;
            c.RollUp.HostDown = c.MoveUp.HostDown;
            c.RollDown.HostDown = c.MoveDown.HostDown;
            c.RollRight.HostDown = c.MoveRight.HostDown;
            c.RolltLeft.HostDown = c.MoveLeft.HostDown;
            c.Shoot.HostDown = (b & CampaignButtons.Shoot) != 0;
            c.Jump.HostDown = (b & CampaignButtons.Jump) != 0;
            c.Morph.HostDown = (b & CampaignButtons.Morph) != 0;
            c.Boost.HostDown = (b & CampaignButtons.Boost) != 0;
            c.AltAttack.HostDown = (b & CampaignButtons.AltAttack) != 0;
            c.Zoom.HostDown = (b & CampaignButtons.Zoom) != 0;
            c.ScanVisor.HostDown = (b & CampaignButtons.ScanVisor) != 0;
            c.Scan.HostDown = (b & CampaignButtons.Scan) != 0;
            c.NextWeapon.HostDown = (b & CampaignButtons.NextWeapon) != 0;
            c.PrevWeapon.HostDown = (b & CampaignButtons.PrevWeapon) != 0;
            c.WeaponMenu.HostDown = false; // the wheel is resolved on the client into a direct weapon key
            c.AffinitySlot.HostDown = (b & CampaignButtons.AffinitySlot) != 0;
            c.PowerBeam.HostDown = select == BeamType.PowerBeam;
            c.VoltDriver.HostDown = select == BeamType.VoltDriver;
            c.Missile.HostDown = select == BeamType.Missile;
            c.Battlehammer.HostDown = select == BeamType.Battlehammer;
            c.Imperialist.HostDown = select == BeamType.Imperialist;
            c.Judicator.HostDown = select == BeamType.Judicator;
            c.Magmaul.HostDown = select == BeamType.Magmaul;
            c.ShockCoil.HostDown = select == BeamType.ShockCoil;
            c.OmegaCannon.HostDown = select == BeamType.OmegaCannon;
        }
    }
}
