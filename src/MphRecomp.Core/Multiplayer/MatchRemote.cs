using System;
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
        // the host's recording taps (Replays O10): every input the network applies to a seat, in the order applied, and
        // every seat dropped. Everything the network changes in the host's sim goes through these two calls.
        public Action<int, NetInput>? RemoteInputTap;
        public Action<int>? SeatDropTap;
        // (O5 rejoin) a dropped seat back in the match
        public Action<int>? SeatRejoinTap;

        public void SetRemoteInput(int seat, in NetInput input)
        {
            PlayerEntity p = PlayerEntity.Players[seat];
            if (!p.HostRemote)
            {
                return;
            }
            // N4: a view older than the cap counts as the cap's (before the tap, so a replay rewinds the same at any cap)
            NetInput applied = input;
            CountLag(seat, input, p.NetAimEpoch); // (for the match host's log only: MatchRemote.LagStats.cs)
            if (applied.ViewTick != 0 && Frame - applied.ViewTick > LagCompensation.MaxRewind)
            {
                applied.ViewTick = (uint)(Frame - LagCompensation.MaxRewind);
            }
            RemoteInputTap?.Invoke(seat, applied);
            if (seat >= 0 && seat < RemoteSeq.Length)
            {
                RemoteSeq[seat] = input.Seq; // (the shot check: Net/ShotCheck.cs)
                RemoteView[seat] = input.ViewTick; // (the hit claims' check: Net/HitClaims.cs; as sent, before the cap)
            }
            SetControls(p.Controls, input.MoveX, input.MoveY, input.Buttons, input.SelectWeapon);
            p.HostZoomButtons((input.Buttons & CampaignButtons.ZoomStep) != 0, (input.Buttons & CampaignButtons.ZoomIn) != 0,
                (input.Buttons & CampaignButtons.ZoomOut) != 0); // ZOOM STEP (CampaignHost.CampaignButtons)
            // T11: the touch controls' stylus (PlayerTouch.cs: touch roll, flick boost, double-tap jump)
            p.HostTouchDown = input.Touching;
            p.HostTouchDx = input.TouchDx;
            p.HostTouchDy = input.TouchDy;
            p.HostRemoteYaw = input.Yaw;
            p.HostRemotePitch = input.Pitch;
            // the client's view counts only once it has taken the game's latest re-aim (spawn/teleport)
            p.HostRemoteAimEpoch = (byte)p.NetAimEpoch == input.AimEpoch ? p.NetAimEpoch : -1;
            // N4: how far back this player's view was (online inputs only; from the input and the frame, so a replay
            // rewinds the same)
            p.HostLagRewind = LagCompensation.RewindFor(Frame, applied.ViewTick);
        }

        // each remote seat's last applied input's sequence number
        public readonly ushort[] RemoteSeq = new ushort[16];
        // and its view tick (0 = none)
        public readonly uint[] RemoteView = new uint[16];

        // N4 lag compensation (Net/LagCompensation): on by default for a match host; the PC tests turn it off to compare
        public bool LagCompOn { get; set; } = true;
        public LagCompensation? Lag => _lag;
        private LagCompensation? _lag;
        private Func<PlayerEntity, PlayerEntity, OpenTK.Mathematics.Vector3>? _lagShift;

        // every frame, before the sim runs (CampaignHost.Step): a match host keeps the seats' poses; nothing is rewound
        // unless an online input carried a view (LAN, bots and the host's own player never do)
        private void LagFrame()
        {
            if (Match == null || Match.IsClient)
            {
                PlayerEntity.HostLagShift = null;
                return;
            }
            _lag ??= new LagCompensation();
            _lagShift ??= _lag.Shift;
            PlayerEntity.HostLagShift = LagCompOn ? _lagShift : null;
            _lag.Record(Frame);
        }

        // a remote player left: the game takes the seat out of the match (PlayerProcess: Disconnected, not Active)
        public void DisconnectSeat(int seat)
        {
            SeatDropTap?.Invoke(seat);
            PlayerEntity p = PlayerEntity.Players[seat];
            p.LoadFlags &= ~LoadFlags.Connected;
            p.HostTouchDown = false;
            SetControls(p.Controls, 0, 0, CampaignButtons.None, BeamType.None);
        }

        // a remote player who was dropped came back (O5 rejoin: its app restarted, or it joined again): the seat is in the
        // match again, its player set up afresh, and it spawns as after a death
        public void ReconnectSeat(int seat)
        {
            SeatRejoinTap?.Invoke(seat);
            PlayerEntity.Players[seat].NetReconnect();
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
            c.PirateBlaster.HostDown = select == BeamType.PirateBlaster; // (a guest's blaster pick: PlayerEntity.HostPirateBlasterKey)
        }
    }
}
