using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // Network play (MphRecomp's multiplayer host). A remote human seat is fed by the host every frame -- its buttons
    // through its own Controls.*.HostDown, its view through an ABSOLUTE yaw/pitch target that ProcessHostInput turns
    // into this frame's aim delta. Absolute targets can't drift when a packet is lost: any error is gone next frame.
    // Nothing here does anything unless the host marks a seat HostRemote.
    public partial class PlayerEntity
    {
        public bool HostRemote { get; set; }

        // the remote player's view: yaw in degrees (the gun vector's heading, atan2(x, z)) and pitch in the game's own
        // aim units (_aimY, degrees, clamped by the game); only applied while the epoch matches (see NetAimEpoch)
        public float HostRemoteYaw { get; set; }
        public float HostRemotePitch { get; set; }
        public int HostRemoteAimEpoch { get; set; } = -1;

        // bumps whenever the game re-aims the player itself (spawn, respawn, teleport): a client's target from before
        // that is stale and is ignored until the client adopts the new view and echoes the new epoch
        public int NetAimEpoch { get; private set; }

        public float NetYaw => MathHelper.RadiansToDegrees(MathF.Atan2(_gunVec1.X, _gunVec1.Z));
        public float NetPitch => _aimY;
        public Vector3 NetAimVector => _gunVec1;

        // host aim deltas (mouse units, as HostAimX/Y) that move the current aim onto the remote target this frame;
        // mirrors UpdateAimX/Y: degrees = -delta / 4 * sensitivity (zoomed sensitivity scales with the FOV)
        private (float X, float Y) HostRemoteAimDelta()
        {
            if (HostRemoteAimEpoch != NetAimEpoch)
            {
                return (0, 0);
            }
            float sensitivity = 1;
            if (EquipInfo.Zoomed)
            {
                float fovFactor = CameraInfo.Fov - Fixed.ToFloat(Values.NormalFov) * 2;
                if (fovFactor != 0)
                {
                    sensitivity /= -Fixed.ToFloat(Values.Field70) * fovFactor;
                }
            }
            float yaw = HostRemoteYaw - NetYaw;
            yaw -= 360 * MathF.Floor((yaw + 180) / 360); // shortest way round
            float pitch = HostRemotePitch - _aimY;
            // the wire quantises the target (~0.006 deg yaw, 0.01 deg pitch): once there, send nothing, or the
            // residual would count as input every frame and keep the idle sway from ever starting
            if (MathF.Abs(yaw) < 0.02f)
            {
                yaw = 0;
            }
            if (MathF.Abs(pitch) < 0.02f)
            {
                pitch = 0;
            }
            if (Controls.InvertAimX)
            {
                yaw = -yaw;
            }
            if (Controls.InvertAimY)
            {
                pitch = -pitch;
            }
            return (-4 * yaw / sensitivity, -4 * pitch / sensitivity);
        }

        // ---- match client: this scene mirrors a host ----

        // Set on a match client: damage only ever comes from the host (NetApply), never from this scene's own beams,
        // bombs or hazards.
        public static bool NetClient { get; set; }
        private static bool _netApplyingDamage;
        public static bool NetDamageAllowed => !NetClient || _netApplyingDamage;

        // the buttons that were down this frame, the same for bots and humans (bots set their Controls too)
        public ushort NetControlBits()
        {
            PlayerControls c = Controls;
            int bits = 0;
            if (c.MoveUp.IsDown || c.RollUp.IsDown) bits |= NetControls.Up;
            if (c.MoveDown.IsDown || c.RollDown.IsDown) bits |= NetControls.Down;
            if (c.MoveLeft.IsDown || c.RolltLeft.IsDown) bits |= NetControls.Left;
            if (c.MoveRight.IsDown || c.RollRight.IsDown) bits |= NetControls.Right;
            if (c.Shoot.IsDown) bits |= NetControls.Shoot;
            if (c.Jump.IsDown) bits |= NetControls.Jump;
            if (c.Morph.IsDown) bits |= NetControls.Morph;
            if (c.Boost.IsDown) bits |= NetControls.Boost;
            if (c.AltAttack.IsDown) bits |= NetControls.AltAttack;
            if (c.Zoom.IsDown) bits |= NetControls.Zoom;
            return (ushort)bits;
        }

        public PlayerNetState NetCapture()
        {
            int weapons = 0;
            for (int i = 0; i < 9; i++)
            {
                if (_availableWeapons[i])
                {
                    weapons |= 1 << i;
                }
            }
            return new PlayerNetState
            {
                Flags = (byte)((LoadFlags.TestFlag(LoadFlags.Active) ? PlayerNetState.Active : 0)
                    | (LoadFlags.TestFlag(LoadFlags.Spawned) ? PlayerNetState.Spawned : 0)
                    | (IsAltForm ? PlayerNetState.AltForm : 0)),
                Position = Position,
                Speed = Speed,
                Facing = _facingVector,
                Up = _upVector,
                Yaw = NetYaw,
                Pitch = NetPitch,
                AimEpoch = (byte)NetAimEpoch,
                Health = (ushort)Math.Max(_health, 0),
                Weapon = (sbyte)CurrentWeapon,
                Weapons = (ushort)weapons,
                Ammo0 = (short)_ammo[0],
                Ammo1 = (short)_ammo[1],
                Controls = NetControlBits(),
                LastAttacker = NetLastAttacker,
                LastBeam = (sbyte)NetLastBeam,
                LastFlags = (byte)NetLastFlags
            };
        }

        // the last hit this player took (host): who, with what, how -- a client's kill messages need them
        public sbyte NetLastAttacker { get; private set; } = -1;
        public BeamType NetLastBeam { get; private set; } = BeamType.Platform;
        public DamageFlags NetLastFlags { get; private set; }
        private static PlayerEntity? _netDamageAttacker;
        private static BeamType _netDamageBeam = BeamType.Platform;

        private void NetRecordHit(PlayerEntity? attacker, BeamProjectileEntity? beam, DamageFlags flags)
        {
            NetLastAttacker = (sbyte)(attacker?.SlotIndex ?? -1);
            NetLastBeam = beam?.Beam ?? BeamType.Platform;
            NetLastFlags = flags & (DamageFlags.Headshot | DamageFlags.Burn | DamageFlags.Deathalt | DamageFlags.Halfturret);
        }

        private int _netFormMismatch;
        private int _netWeaponMismatch;

        // Brings this (client-side) player to the host's state. Damage, death and respawn go through the game's own
        // TakeDamage / Spawn so the flash, sounds, death and spawn effects play; then the host's exact numbers win.
        // pose: false for the local player unless it drifted past poseTolerance (it runs on its own input).
        // returns true when the pose was moved onto the host's
        public bool NetApply(in PlayerNetState s, bool pose, float poseTolerance = 0)
        {
            if ((s.Flags & PlayerNetState.Active) == 0 || !LoadFlags.TestFlag(LoadFlags.Active))
            {
                return false;
            }
            if (s.Health < _health)
            {
                _netApplyingDamage = true;
                _netDamageAttacker = s.LastAttacker >= 0 && s.LastAttacker < MaxPlayers ? Players[s.LastAttacker] : null;
                _netDamageBeam = (BeamType)s.LastBeam;
                try
                {
                    TakeDamage((uint)(_health - s.Health), DamageFlags.IgnoreInvuln | (DamageFlags)s.LastFlags,
                        direction: null, source: null);
                }
                finally
                {
                    _netApplyingDamage = false;
                    _netDamageAttacker = null;
                }
            }
            else if (s.Health > 0 && _health == 0 && (s.Flags & PlayerNetState.Spawned) != 0)
            {
                Spawn(s.Position, s.Facing, s.Up, _scene.GetNodeRefByPosition(s.Position), respawn: true);
                pose = true;
            }
            if (_health > 0 || s.Health == 0)
            {
                _health = s.Health;
            }
            for (int i = 0; i < 9; i++)
            {
                _availableWeapons[i] = (s.Weapons & (1 << i)) != 0;
            }
            _ammo[0] = s.Ammo0;
            _ammo[1] = s.Ammo1;
            // a puppet takes the host's weapon at once; the local player's own switch reaches the host a round trip
            // later, so only a mismatch that outlasts that is corrected
            if (s.Weapon >= 0 && s.Weapon != (sbyte)CurrentWeapon && _health > 0)
            {
                if (pose || ++_netWeaponMismatch > 30)
                {
                    TryEquipWeapon((BeamType)s.Weapon, silent: true);
                    _netWeaponMismatch = 0;
                }
            }
            else
            {
                _netWeaponMismatch = 0;
            }
            // the relayed Morph press normally switches forms here too; correct only a mismatch that persists
            bool altForm = (s.Flags & PlayerNetState.AltForm) != 0;
            if (altForm != IsAltForm && !IsMorphing && !IsUnmorphing && _health > 0)
            {
                if (++_netFormMismatch > 10 && TrySwitchForms(force: true))
                {
                    _netFormMismatch = 0;
                }
            }
            else
            {
                _netFormMismatch = 0;
            }
            // a seat that hasn't spawned on the host yet (or here) has no pose to take
            bool spawned = (s.Flags & PlayerNetState.Spawned) != 0 && LoadFlags.TestFlag(LoadFlags.Spawned);
            if (spawned && (pose || (poseTolerance > 0
                && (s.Position - Position).LengthSquared > poseTolerance * poseTolerance)))
            {
                Vector3 prev = Position;
                Position = s.Position;
                PrevPosition = s.Position;
                Speed = s.Speed;
                _facingVector = s.Facing;
                _upVector = s.Up;
                NodeRef = NodeRef.PartIndex == -1 ? _scene.GetNodeRefByPosition(s.Position)
                    : _scene.UpdateNodeRef(NodeRef, prev, s.Position);
                return true;
            }
            return false;
        }
    }

    public static class NetControls
    {
        public const int Up = 1, Down = 2, Left = 4, Right = 8, Shoot = 16, Jump = 32, Morph = 64, Boost = 128,
            AltAttack = 256, Zoom = 512;
    }

    // One player as the host sees it after a frame (MphRecomp's snapshot; serialised by MphRecomp.Core Net/).
    public struct PlayerNetState
    {
        public const byte Active = 1, Spawned = 2, AltForm = 4;
        public byte Flags;
        public Vector3 Position;
        public Vector3 Speed;
        public Vector3 Facing;
        public Vector3 Up;
        public float Yaw;
        public float Pitch;
        public byte AimEpoch;
        public ushort Health;
        public sbyte Weapon;
        public ushort Weapons;
        public short Ammo0;
        public short Ammo1;
        public ushort Controls;
        public sbyte LastAttacker; // seat, -1 none
        public sbyte LastBeam;
        public byte LastFlags; // DamageFlags: headshot / burn / deathalt / halfturret
    }
}
