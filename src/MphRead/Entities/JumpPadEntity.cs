using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public class JumpPadEntity : EntityBase
    {
        private readonly JumpPadEntityData _data;
        private readonly Matrix4 _beamTransform;
        private readonly Vector3 _beamVector;
        private CollisionVolume _volume;
        private Vector3 _prevPos;

        private bool _invSetUp = false;
        private EntityBase? _parent = null;
        private Vector3 _invPos;

        private ushort _cooldownTimer = 0;

        public NodeData3? ClosestNode { get; set; } = null;

        // host hook (Echoes arenas): pads whose Unused28 has this bit fly every form on the biped's arc (PlayerProcess
        // ActivateJumpPad, PlayerInput gravity); 0 = off. Vanilla pads hold 0 or 2 there, never the host's bit
        public static uint HostSameArcBit { get; set; }
        // host hook: on a same-arc flight the stick's left/right drifts across the push without eating it, forward/back stay
        // MPH's rule, and the stick comes back after the minimum (PlayerInput SameArcSteer/SameArcCut, PlayerProcess); false
        // = MPH's own steering on those flights too. Owner 10-08 ~01:10 picked this Echoes-only left/right rule for the long
        // Echoes jumps (MPH's rule: a 0.25 s sideways tap fell ~20 short); Tools env PAD_MPHSTEER=1 turns it off to compare
        public static bool HostSameArcSteer { get; set; } = true;
        public bool SameArc => HostSameArcBit != 0 && (_data.Unused28 & HostSameArcBit) != 0;

        public JumpPadEntity(JumpPadEntityData data, string nodeName, Scene scene)
            : base(EntityType.JumpPad, nodeName, scene)
        {
            _data = data;
            Id = data.Header.EntityId;
            SetTransform(data.Header.FacingVector, data.Header.UpVector, data.Header.Position);
            _prevPos = Position;
            _volume = CollisionVolume.Move(_data.Volume, Position);
            string modelName = Metadata.JumpPads[(int)data.ModelId];
            SetUpModel(modelName);
            ModelInstance beamInst = SetUpModel("JumpPad_Beam");
            Vector3 beamVector = data.BeamVector.ToFloatVector().Normalized();
            _beamTransform = GetTransformMatrix(beamVector, beamVector.X != 0 || beamVector.Z != 0 ? Vector3.UnitY : Vector3.UnitX);
            _beamTransform.Row3.Y = 0.25f;
            _beamVector = Matrix.Vec3MultMtx3(beamVector, Transform) * _data.Speed.FloatValue;
            if (GameState.Mode == GameMode.SinglePlayer)
            {
                Active = GameState.StorySave.InitRoomState(_scene.RoomId, Id, active: data.Active != 0) != 0;
            }
            else
            {
                Active = data.Active != 0;
            }
            beamInst.Active = Active;
        }

        public override void Initialize()
        {
            base.Initialize();
            if (_data.ParentId != -1)
            {
                if (_scene.TryGetEntity(_data.ParentId, out EntityBase? parent))
                {
                    _parent = parent;
                }
            }
        }

        public override bool GetTargetable()
        {
            return false;
        }

        public override bool Process()
        {
            if (_parent != null)
            {
                if (!_invSetUp)
                {
                    _invPos = Matrix.Vec3MultMtx4(Position, _parent.CollisionTransform.Inverted());
                    _invSetUp = true;
                }
                Position = Matrix.Vec3MultMtx4(_invPos, _parent.CollisionTransform);
            }
            if (_prevPos != Position)
            {
                _volume = CollisionVolume.Move(_data.Volume, Position);
                _prevPos = Position;
            }
            if (Active && _cooldownTimer == 0)
            {
                foreach (PlayerEntity player in _scene.GetPlayerEntities())
                {
                    if (player.Health == 0)
                    {
                        continue;
                    }
                    Vector3 position;
                    if (player.IsAltForm)
                    {
                        if (!_data.TriggerFlags.TestFlag(TriggerFlags.PlayerAlt))
                        {
                            continue;
                        }
                        position = player.Volume.SpherePosition;
                    }
                    else
                    {
                        if (!_data.TriggerFlags.TestFlag(TriggerFlags.PlayerBiped))
                        {
                            continue;
                        }
                        position = player.Position;
                    }
                    if (_volume.TestPoint(position))
                    {
                        player.ActivateJumpPad(this, _beamVector, _data.ControlLockTime);
                        _cooldownTimer = (ushort)(_data.CooldownTime * 2); // todo: FPS stuff
                    }
                }
            }
            if (_cooldownTimer > 0)
            {
                _cooldownTimer--;
            }
            return base.Process();
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (info.Message == Message.Activate)
            {
                Active = true;
                if (GameState.Mode == GameMode.SinglePlayer)
                {
                    GameState.StorySave.SetRoomState(_scene.RoomId, Id, state: 3);
                }
            }
            else if (info.Message == Message.SetActive)
            {
                if ((int)info.Param1 != 0)
                {
                    Active = true;
                    if (GameState.Mode == GameMode.SinglePlayer)
                    {
                        GameState.StorySave.SetRoomState(_scene.RoomId, Id, state: 3);
                    }
                }
                else
                {
                    Active = false;
                    if (GameState.Mode == GameMode.SinglePlayer)
                    {
                        GameState.StorySave.SetRoomState(_scene.RoomId, Id, state: 1);
                    }
                }
            }
            _models[1].Active = Active;
        }

        protected override Matrix4 GetModelTransform(ModelInstance inst, int index)
        {
            if (index == 1)
            {
                return Matrix4.CreateScale(inst.Model.Scale) * _beamTransform * _transform;
            }
            return base.GetModelTransform(inst, index);
        }

        public override void GetDrawInfo()
        {
            if (IsVisible(NodeRef))
            {
                base.GetDrawInfo();
            }
        }

        public override void GetDisplayVolumes()
        {
            if (_scene.ShowVolumes == VolumeDisplay.JumpPad)
            {
                AddVolumeItem(_volume, Vector3.UnitY);
            }
        }

        public override void SetActive(bool active)
        {
            base.SetActive(active);
            _models[1].Active = Active;
        }
    }

    public class FhJumpPadEntity : EntityBase
    {
        private readonly FhJumpPadEntityData _data;
        private readonly Matrix4 _beamTransform;

        private readonly CollisionVolume _volume;

        public FhJumpPadEntity(FhJumpPadEntityData data, Scene scene) : base(EntityType.FhJumpPad, scene)
        {
            _data = data;
            Id = data.Header.EntityId;
            SetTransform(data.Header.FacingVector, data.Header.UpVector, data.Header.Position);
            _volume = CollisionVolume.Move(_data.ActiveVolume, Position);
            string name = data.ModelId == 1 ? "balljump" : "jumppad_base";
            SetUpModel(name, firstHunt: true);
            name = data.ModelId == 1 ? "balljump_ray" : "jumppad_ray";
            SetUpModel(name, firstHunt: true);
            Vector3 beamVector = data.BeamVector.ToFloatVector().Normalized();
            _beamTransform = GetTransformMatrix(beamVector, beamVector.X != 0 || beamVector.Z != 0 ? Vector3.UnitY : Vector3.UnitX);
        }

        protected override Matrix4 GetModelTransform(ModelInstance inst, int index)
        {
            if (index == 1)
            {
                // FH beam vectors are absolute, so don't include the parent rotation
                // todo: it would be nicer to just compute beamTransform as relative on creation
                Matrix4 transform = Matrix4.CreateScale(inst.Model.Scale) * _beamTransform;
                transform.Row3.Xyz = Position.AddY(0.25f);
                return transform;
            }
            return base.GetModelTransform(inst, index);
        }

        public override void GetDisplayVolumes()
        {
            if (_scene.ShowVolumes == VolumeDisplay.JumpPad)
            {
                AddVolumeItem(_volume, Vector3.UnitY);
            }
        }
    }
}
