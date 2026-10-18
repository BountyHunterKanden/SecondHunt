using System.Collections.Generic;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: First Hunt's world entities (docs/FIRST_HUNT_PORT.md P2; fh_research p2_world.md), on MphRead's display
    // stubs while a First Hunt level runs (HostFirstHunt). Messages: FirstHuntMessages.

    // door (cart 0x2020964): opens while a player is within 4.0 and it isn't locked; Unlock (16) unlocks it for good;
    // shots don't open it. Closed, it stops players at its plane (0x200d530).
    public partial class FhDoorEntity
    {
        private bool _fhLocked;
        private bool _fhOpen = false;

        public bool FirstHuntLocked => _fhLocked;
        public bool FirstHuntOpen => _fhOpen;
        public FhDoorEntityData Data => _data;

        public override void Initialize()
        {
            base.Initialize();
            _fhLocked = _data.Locked != 0;
        }

        public override bool Process()
        {
            if (!PlayerEntity.HostFirstHunt)
            {
                return base.Process();
            }
            bool open = false;
            if (!_fhLocked)
            {
                foreach (PlayerEntity player in _scene.GetPlayerEntities())
                {
                    if (player.Health > 0 && Vector3.DistanceSquared(player.Position, Position) < 4 * 4)
                    {
                        open = true;
                        break;
                    }
                }
            }
            if (open != _fhOpen)
            {
                _fhOpen = open;
                AnimationInfo anim = _models[0].AnimInfo;
                anim.Flags[0] &= ~(AnimFlags.Ended | AnimFlags.Paused);
                if (open)
                {
                    anim.Flags[0] &= ~AnimFlags.Reverse;
                    _soundSource.PlaySfx(SfxId.DOOR_OPEN); // (First Hunt's 0x35)
                    FirstHuntMission.MusicCue(1);
                }
                else
                {
                    anim.Flags[0] |= AnimFlags.Reverse;
                    _soundSource.PlaySfx(SfxId.DOOR_CLOSE); // (0x34)
                }
                anim.Flags[1] = anim.Flags[0];
            }
            _soundSource.Update(Position, rangeIndex: 6);
            return base.Process();
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (PlayerEntity.HostFirstHunt && FirstHuntMessages.Of(info) == FhMessage.Unlock && _fhLocked)
            {
                _fhLocked = false;
                _soundSource.PlayFreeSfx(SfxId.DOOR_UNLOCK); // (0x45 + 0x38)
                FirstHuntMission.MusicCue(2);
            }
        }

        // a closed door is a plane through its position, facing its facing, for players within 2 (0x200d530)
        public void BlockPlayer(PlayerEntity player, ref Vector3 position, float radius)
        {
            if (_fhOpen || Vector3.DistanceSquared(position, Position) >= 2 * 2)
            {
                return;
            }
            Vector3 normal = FacingVector;
            float side = Vector3.Dot(position - Position, normal);
            float min = radius + 0.2f;
            if (side >= 0 && side < min)
            {
                position += normal * (min - side);
            }
            else if (side < 0 && side > -min)
            {
                position -= normal * (min + side);
            }
        }
    }

    // trigger volume (cart 0x2028ba0): a player's beams / the player unmorphed / morphed (flags 1/2/4) in the volume
    // fire it; threshold triggers (subtype 3) fire on the Threshold-th Trigger message (one per spawner pointing at
    // them). Firing sends ParentMessage and ChildMessage once; not OneUse, it fires again every Cooldown+2 frames while
    // still being hit.
    public partial class FhTriggerVolumeEntity
    {
        private bool _fhTriggered = false;
        private int _fhTimer = 0;
        private int? _fhCounter = null;

        public int FirstHuntFired { get; private set; }

        public override bool Process()
        {
            if (PlayerEntity.HostFirstHunt && _data.Subtype != FhTriggerType.Threshold)
            {
                foreach (PlayerEntity player in _scene.GetPlayerEntities())
                {
                    if (_data.TriggerFlags.TestFlag(FhTriggerFlags.Beam))
                    {
                        foreach (BeamProjectileEntity beam in _scene.GetBeamProjectileEntities())
                        {
                            if (beam.Owner == player && beam.Lifespan > 0 && _volume.TestPoint(beam.Position))
                            {
                                Fire();
                            }
                        }
                    }
                    if (player.Health > 0
                        && (_data.TriggerFlags.TestFlag(FhTriggerFlags.PlayerBiped) && !player.IsAltForm
                        || _data.TriggerFlags.TestFlag(FhTriggerFlags.PlayerAlt) && player.IsAltForm)
                        && _volume.TestPoint(player.Position))
                    {
                        Fire();
                    }
                }
            }
            return base.Process();
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (PlayerEntity.HostFirstHunt && _data.Subtype == FhTriggerType.Threshold && FirstHuntMessages.Of(info) == FhMessage.Trigger)
            {
                _fhCounter ??= (int)(_data.Threshold > 0 ? _data.Threshold - 1 : 0);
                if (_fhCounter > 0)
                {
                    _fhCounter--;
                }
                else
                {
                    Fire();
                    _fhCounter = (int)(_data.Threshold > 0 ? _data.Threshold - 1 : 0);
                }
            }
        }

        private void Fire()
        {
            if (!_fhTriggered)
            {
                if (_data.ParentMessage != FhMessage.None)
                {
                    _fhTriggered = true;
                    FirstHuntMessages.Send(_scene, _data.ParentMessage, this, _parent, (ushort)_data.ParentMsgParam1);
                }
                if (_data.ChildMessage != FhMessage.None)
                {
                    _fhTriggered = true;
                    FirstHuntMessages.Send(_scene, _data.ChildMessage, this, _child, (ushort)_data.ChildMsgParam1);
                }
                if (_fhTriggered)
                {
                    FirstHuntFired++;
                }
            }
            else if (_data.OneUse == 0)
            {
                if (_fhTimer > 0)
                {
                    _fhTimer--;
                }
                else
                {
                    _fhTriggered = false;
                    _fhTimer = _data.Cooldown * 2; // todo: FPS stuff
                }
            }
        }
    }

    // area volume (cart 0x2029034): Inside when a player enters (again every Cooldown frames while inside, if it has
    // one), Exit when they leave; Damage / Death / Gravity go to that player, anything else is a global event. (Subtype
    // 2 reads the box: the cart's bug.) Every shipped level has one: a kill plane (Inside = Death).
    public partial class FhAreaVolumeEntity
    {
        private readonly bool[] _fhInside = new bool[PlayerEntity.SlotCapacity];
        private readonly int[] _fhTimers = new int[PlayerEntity.SlotCapacity];
        private CollisionVolume _fhVolume;
        private bool _fhVolumeSet = false;

        public override bool Process()
        {
            if (!PlayerEntity.HostFirstHunt)
            {
                return base.Process();
            }
            if (!_fhVolumeSet)
            {
                _fhVolume = CollisionVolume.Move(_data.Subtype == FhTriggerType.Sphere ? _data.Sphere : _data.Box, Position);
                _fhVolumeSet = true;
            }
            int cooldown = _data.Cooldown > 0 ? (_data.Cooldown - 1) * 2 : 0; // todo: FPS stuff
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                int slot = player.SlotIndex;
                bool form = _data.TriggerFlags.TestFlag(FhTriggerFlags.PlayerBiped) && !player.IsAltForm
                    || _data.TriggerFlags.TestFlag(FhTriggerFlags.PlayerAlt) && player.IsAltForm;
                if (form && player.Health > 0 && _fhVolume.TestPoint(player.Position))
                {
                    if (!_fhInside[slot])
                    {
                        _fhInside[slot] = true;
                        Send(player, _data.InsideMessage, _data.InsideMsgParam1, inside: true);
                    }
                    else if (_data.Cooldown != 0)
                    {
                        if (_fhTimers[slot] > 0)
                        {
                            _fhTimers[slot]--;
                        }
                        else
                        {
                            Send(player, _data.InsideMessage, _data.InsideMsgParam1, inside: true);
                            _fhTimers[slot] = cooldown;
                        }
                    }
                }
                else if (_fhInside[slot])
                {
                    _fhInside[slot] = false;
                    _fhTimers[slot] = cooldown;
                    Send(player, _data.ExitMessage, _data.ExitMsgParam1, inside: false);
                }
            }
            return base.Process();
        }

        private void Send(PlayerEntity player, FhMessage message, int param1, bool inside)
        {
            switch (message)
            {
            case FhMessage.None:
            case FhMessage.Unknown21 when inside:
                break;
            case FhMessage.Damage:
                _scene.SendMessage(Message.Damage, this, player, param1, 0);
                break;
            case FhMessage.Death:
                _scene.SendMessage(Message.Death, this, player, param1, 0);
                break;
            case FhMessage.Gravity when inside:
                _scene.SendMessage(Message.Gravity, this, player, param1, 0);
                break;
            default:
                FirstHuntMessages.Send(_scene, message, this, target: null, param1, delay: 1);
                break;
            }
        }
    }

    // platform (cart 0x2029908): idle until a trigger volume's global Activate carries its GroupId
    public partial class FhPlatformEntity
    {
        public FhPlatformEntityData Data => _data;

        private bool FirstHuntActivated()
        {
            if (!PlayerEntity.HostFirstHunt)
            {
                return true;
            }
            foreach ((EntityBase sender, int param1) in FirstHuntMessages.Poll(_scene, FhMessage.Activate))
            {
                if (sender.Type == EntityType.FhTriggerVolume && param1 == _data.GroupId)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // point module: First Hunt's chain starts at the module marked Active (MPH's at id 50); the pickup and scoring
    // are the player's (PlayerFirstHunt)
    public partial class PointModuleEntity
    {
        private bool FirstHuntStart => PlayerEntity.HostFirstHunt && _data.Active != 0;

        // First Hunt drops a link to the module itself (link 0x202a8dc): the last modules link to themselves
        private bool FirstHuntSelfLink(int id) => PlayerEntity.HostFirstHunt && id == Id;

        public static void ResetCurrent()
        {
            _current = null;
        }
    }
}
