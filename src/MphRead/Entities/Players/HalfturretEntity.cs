using System;
using System.Diagnostics;
using MphRead.Effects;
using MphRead.Formats;
using MphRead.Formats.Culling;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public class HalfturretEntity : DynamicLightEntityBase
    {
        public PlayerEntity Owner { get; }
        private EntityBase? _target = null;
        public EntityBase? Target => _target;
        public NodeData3? ClosestNode { get; set; } = null;

        private int _health = 0;
        private ushort _timeSinceDamage = UInt16.MaxValue;
        private ushort _timeSinceFrozen = 0;
        private ushort _freezeTimer = 0;
        private ushort _burnTimer = 0;
        private EffectEntry? _burnEffect = null;

        public int Health { get => _health; set => _health = value; }
        public ushort TimeSinceDamage { get => _timeSinceDamage; set => _timeSinceDamage = value; }

        private float _ySpeed = 0;
        private bool _grounded = false;
        private Vector3 _aimVector;
        private ushort _targetTimer = 0; // todo: is this always zero?
        private ushort _cooldownTimer = 0;
        private float _cooldownFactor = 1.5f;

        public EquipInfo EquipInfo { get; } = new EquipInfo();

        private Node _baseNode = null!;
        private Node _baseNodeParent = null!;
        private ModelInstance _altIceModel = null!;

        // recomp: the Trooper's Scout Drone (PlayerScoutDrone.cs): with Owner.HostScoutDrone this half is his standing
        // body, not a turret: it never fires and is drawn as Weavel's biped in its idle (HostBodyModel) where he split,
        // unless the host draws it (HostBodyByApp: the seat's HD skin). HostBodyPosition = where his own Position would be
        // for that body (MphRead's biped draw and the host's skins place a body from it).
        private ModelInstance? _bodyModel = null;
        public ModelInstance? HostBodyModel => _bodyModel;
        public bool HostBodyByApp { get; set; }
        public Vector3 HostBodyPosition => Position.AddY(-0.45f - Fixed.ToFloat(Owner.Values.MinPickupHeight));

        // recomp: hits on the standing body (owner 2026-10-09): a segment `radius` wide (a beam's, the drone laser's) hits
        // the whole biped standing there -- a player's own standing cylinder (BeamProjectileEntity), MinPickupHeight to
        // MaxPickupHeight around HostBodyPosition, BipedColRadius wide -- not the turret's 0.45 sphere. Still a Halfturret
        // hit; its headshots: HostBodyHeadshot.
        public bool HostBodyHit(Vector3 from, Vector3 to, float radius, ref CollisionResult result)
        {
            float minY = Fixed.ToFloat(Owner.Values.MinPickupHeight);
            float height = Fixed.ToFloat(Owner.Values.MaxPickupHeight) - minY;
            return CollisionDetection.CheckCylindersOverlap(from, to, HostBodyPosition.AddY(minY), Vector3.UnitY, height,
                Fixed.ToFloat(Owner.Values.BipedColRadius) + radius, ref result);
        }

        // recomp: a hit at `at` on the standing body is in its head zone (owner 2026-10-15): the top 0.3 of that cylinder,
        // measured from HostBodyPosition as a standing hunter's is from his Position (BeamProjectileEntity's own test)
        public bool HostBodyHeadshot(Vector3 at)
        {
            return Owner.HostScoutDrone && Owner.HostDroneBodyHeadshots
                && at.Y - HostBodyPosition.Y >= Fixed.ToFloat(Owner.Values.MaxPickupHeight) - 0.3f;
        }

        public HalfturretEntity(PlayerEntity owner, Scene scene) : base(EntityType.Halfturret, scene)
        {
            Owner = owner;
        }

        public void Create()
        {
            ModelInstance inst = SetUpModel("WeavelAlt_Turret_lod0");
            _baseNode = inst.Model.GetNodeByName("TurretBase")!;
            _baseNodeParent = inst.Model.Nodes[_baseNode.ParentIndex];
            _altIceModel = SetUpModel("alt_ice");
        }

        public override void Initialize()
        {
            Recolor = Owner.Recolor;
            base.Initialize();
            float minY = Fixed.ToFloat(Owner.Values.MinPickupHeight);
            Vector3 position = Owner.Position.AddY(minY + 0.45f);
            var facing = new Vector3(Owner.Field70, 0, Owner.Field74);
            Transform = GetTransformMatrix(facing, Vector3.UnitY, position);
            _aimVector = facing;
            NodeRef = Owner.NodeRef;
            int health = Owner.Health;
            if (health > 1)
            {
                _health = health / 2;
                Owner.Health -= _health;
            }
            else
            {
                _health = 1;
            }
            _grounded = Owner.Flags1.TestFlag(PlayerFlags1.Standing);
            EquipInfo.Beams = Owner.EquipInfo.Beams;
            EquipInfo.Weapon = Weapons.Current[3]; // non-affinity Battlehammer
            _models[0].SetAnimation(1, AnimFlags.NoLoop);
            if (Owner.HostScoutDrone && _bodyModel == null)
            {
                _bodyModel = SetUpModel(Metadata.HunterModels[Hunter.Weavel][0], (int)PlayerAnimation.Idle);
            }
            _light1Vector = Owner.Light1Vector;
            _light1Color = Owner.Light1Color;
            _light2Vector = Owner.Light2Vector;
            _light2Color = Owner.Light2Color;
            _scanId = PlayerEntity.ScanIds[(int)Hunter.Weavel, 1];
        }

        public override void GetVectors(out Vector3 position, out Vector3 up, out Vector3 facing)
        {
            position = Position;
            up = Vector3.UnitY;
            facing = FacingVector;
        }

        public void Reposition(Vector3 offset, NodeRef nodeRef)
        {
            Position += offset;
            _target = null;
            _targetTimer = 0;
            NodeRef = nodeRef;
        }

        public void OnTakeDamage(EntityBase attacker, uint damage)
        {
            _target = attacker;
            _targetTimer = 30 * 2; // todo: FPS stuff
            _cooldownFactor -= 61 * damage;
            if (_cooldownFactor < 0.7f)
            {
                _cooldownFactor = 0.7f;
            }
        }

        public void OnFrozen()
        {
            // todo: FPS stuff
            if (_timeSinceFrozen > 60 * 2)
            {
                _freezeTimer = 75 * 2;
            }
            else if (_freezeTimer < 15 * 2)
            {
                _freezeTimer = 15 * 2;
            }
        }

        public void OnSetOnFire()
        {
            _burnTimer = 150 * 2; // todo: FPS stuff
            if (_burnEffect != null)
            {
                _scene.UnlinkEffectEntry(_burnEffect);
                _burnEffect = null;
            }
            _burnEffect = _scene.SpawnEffectGetEntry(187, FacingVector.WithY(0), Vector3.UnitY, Position); // flamingAltForm
            _burnEffect?.SetElementExtension(true);
        }

        public override bool Process()
        {
            if (_health == 0 || !Owner.Flags2.TestFlag(PlayerFlags2.Halfturret))
            {
                return false;
            }
            if (_burnTimer > 0)
            {
                _burnTimer--;
                if (_burnTimer % (8 * 2) == 0) // todo: FPS stuff
                {
                    Owner.TakeDamage(1, DamageFlags.NoSfx | DamageFlags.Burn | DamageFlags.NoDmgInvuln | DamageFlags.Halfturret,
                        direction: null, Owner.BurnedBy);
                }
                if (_burnEffect != null)
                {
                    Vector3 facing = FacingVector;
                    facing = new Vector3(facing.X, 0, facing.Z);
                    _burnEffect.Transform(facing, Vector3.UnitY, Position);
                }
            }
            else if (_burnEffect != null)
            {
                _scene.UnlinkEffectEntry(_burnEffect);
                _burnEffect = null;
            }
            if (_freezeTimer == 0)
            {
                base.Process();
                if (_targetTimer > 0)
                {
                    _targetTimer--;
                }
                else
                {
                    _target = null;
                }
                if (_cooldownFactor < 1.5f)
                {
                    _cooldownFactor = Math.Min(_cooldownFactor + 0.015f / 2, 1.5f); // todo: FPS stuff
                }
                else if (_cooldownFactor > 1.5f)
                {
                    _cooldownFactor = Math.Max(_cooldownFactor - 0.015f / 2, 1.5f); // todo: FPS stuff
                }
                if (_target == null)
                {
                    float minDistSqr = 15 * 15;
                    foreach (PlayerEntity player in _scene.GetPlayerEntities())
                    {
                        if (player == Owner || player.Health == 0 || player.TeamIndex == Owner.TeamIndex || player.CurAlpha < 6 / 31f)
                        {
                            continue;
                        }
                        Vector3 between = player.Position - Position;
                        float distSqr = between.LengthSquared;
                        if (distSqr < minDistSqr)
                        {
                            minDistSqr = distSqr;
                            _target = player;
                        }
                    }
                }
                if (_target != null && !Owner.HostScoutDrone) // recomp: the Trooper's standing body never fires
                {
                    Vector3 muzzlePos = Position.AddY(0.4f);
                    int encounter = GameState.EncounterState[Owner.SlotIndex];
                    if (Owner.IsBot && GameState.SinglePlayer
                        && (encounter == 1 || encounter == 3 || encounter == 4))
                    {
                        if (_cooldownTimer > 0)
                        {
                            _cooldownTimer--;
                        }
                        else
                        {
                            _cooldownTimer = 65 * 2; // todo: FPS stuff
                        }

                    }
                    else
                    {
                        _cooldownTimer = 1; // not being used
                    }
                    UpdateAim(muzzlePos, _target.Position, EquipInfo, out _aimVector);
                    float cooldown = EquipInfo.Weapon.ShotCooldown * _cooldownFactor;
                    if (cooldown < 7.5f)
                    {
                        cooldown = 7;
                    }
                    if (Owner.TimeSinceShot >= cooldown * 2 && _cooldownTimer < 60 * 2) // todo: FPS stuff
                    {
                        if (Owner.IsBot && GameState.SinglePlayer
                            && (encounter == 1 || encounter == 3 || encounter == 4))
                        {
                            // no need to set infinite ammo since we don't have an ammo getter/setter
                            EquipInfo.UnchargedDamage = 3;
                            EquipInfo.HeadshotDamage = 3;
                            EquipInfo.SplashDamage = 3;
                            EquipInfo.MinChargeSplashDamage = 3;
                            EquipInfo.ChargedSplashDamage = 3;
                            EquipInfo.DmgDirTypes[0] = 0;
                            EquipInfo.DmgDirTypes[1] = 0;
                        }
                        BeamSpawnFlags flags = Owner.DoubleDamage ? BeamSpawnFlags.DoubleDamage : BeamSpawnFlags.None;
                        BeamResultFlags result = BeamProjectileEntity.Spawn(this, EquipInfo, muzzlePos, _aimVector, flags, NodeRef, _scene);
                        if (result != BeamResultFlags.NoSpawn)
                        {
                            _models[0].SetAnimation(0, AnimFlags.NoLoop);
                            Owner.TimeSinceShot = 0;
                        }
                    }
                }
            }
            else
            {
                _freezeTimer--;
                _timeSinceFrozen = 0;
            }
            if (_timeSinceFrozen != UInt16.MaxValue)
            {
                _timeSinceFrozen++;
            }
            if (_timeSinceDamage != UInt16.MaxValue)
            {
                _timeSinceDamage++;
            }
            if (Owner == PlayerEntity.Main)
            {
                string message = Text.Strings.GetHudMessage(233); // turret energy: %d
                if (Owner.HostScoutDrone)
                {
                    message = TrooperEnergyMessage(message);
                }
                // hide during dialog pause to prevent overlap -- the game doesn't do this, and also can't play as Weavel in 1P anyway
                Owner.QueueHudMessage(128, 150, 1 / 1000f, 0, message.Replace("%d", _health.ToString()), dialogHide: true);
            }
            if (!_grounded)
            {
                // future: it would be cool to have the halfturret move with platforms, etc.
                Vector3 prevPos = Position;
                _ySpeed -= 0.02f / 2; // todo: FPS stuff
                Position = Position.AddY(_ySpeed / 2); // todo: FPS stuff
                var results = new CollisionResult[1];
                if (CollisionDetection.CheckSphereBetweenPoints(prevPos, Position, 0.45f, limit: 1,
                    includeOffset: false, TestFlags.None, _scene, results) > 0)
                {
                    CollisionResult result = results[0];
                    float dot = Vector3.Dot(Position, result.Plane.Xyz) - result.Plane.W;
                    dot = 0.45f - dot;
                    Position = result.Position + result.Plane.Xyz * dot;
                    _ySpeed = 0;
                    _grounded = true;
                    // todo?: wifi stuff
                }
                UpdateLightSources(Position);
                NodeRef = _scene.UpdateNodeRef(NodeRef, prevPos, Position);
                ClosestNode = null;
            }
            // todo?: wifi stuff
            Debug.Assert(_scene.Room != null);
            if (Position.Y < _scene.Room.Meta.KillHeight)
            {
                Die();
            }
            return true;
        }

        public void ResetGroundedState()
        {
            _grounded = false;
        }

        public static bool UpdateAim(Vector3 muzzlePos, Vector3 targetPos, EquipInfo equipInfo, out Vector3 aimVector)
        {
            WeaponInfo weapon = equipInfo.Weapon;
            float chargePct = 0;
            // todo: FPS stuff
            if (weapon.Flags.TestFlag(WeaponFlags.CanCharge) && equipInfo.ChargeLevel >= weapon.MinCharge * 2)
            {
                chargePct = (equipInfo.ChargeLevel - weapon.MinCharge * 2) / (weapon.FullCharge * 2 - weapon.MinCharge * 2);
            }
            aimVector = targetPos - muzzlePos;
            float hMagSqr = aimVector.X * aimVector.X + aimVector.Z * aimVector.Z;
            float hMag = MathF.Sqrt(hMagSqr);
            // todo?: seems okay without FPS stuff here, but we need to confirm
            float uncSpeed = Fixed.ToFloat(weapon.UnchargedSpeed);
            float speed = (Fixed.ToFloat(weapon.MinChargeSpeed) - uncSpeed) * chargePct;
            float uncGravity = Fixed.ToFloat(weapon.UnchargedGravity);
            float gravity = (Fixed.ToFloat(weapon.MinChargeGravity) - uncGravity) * chargePct;
            float v23 = hMagSqr * (uncGravity + gravity) / ((uncSpeed + speed) * (uncSpeed + speed));
            float v24 = v23 / 2;
            bool result = true;
            if (v24 >= 1 / 4096f || v24 <= -1 / 4096f) // v24 != 0
            {
                if ((Fixed.ToInt(v23) & 1) == 1)
                {
                    v23 -= 1 / 4096f;
                }
                float v26 = hMagSqr - 4 * v24 * (v24 - aimVector.Y);
                if (v26 > 0)
                {
                    aimVector.Y = (MathF.Sqrt(v26) - hMag) / v23 * hMag;
                }
                else if (v26 > -1 / 4096f) // v26 == 0
                {
                    aimVector.Y = -hMag / v23 * hMag;
                }
                else // v26 < 0
                {
                    aimVector.Y = hMag;
                    result = false;
                }
            }
            if (aimVector != Vector3.Zero)
            {
                aimVector = aimVector.Normalized();
            }
            else
            {
                aimVector = Vector3.UnitX;
            }
            return result;
        }

        public void Die()
        {
            Owner.OnHalfturretDied();
            if (_health > 0)
            {
                _health = 0;
                _scene.SpawnEffect(216, Vector3.UnitX, Vector3.UnitY, Position); // deathAlt
            }
        }

        public override void Destroy()
        {
            if (_burnEffect != null)
            {
                _scene.UnlinkEffectEntry(_burnEffect);
                _burnEffect = null;
            }
            base.Destroy();
        }

        public override void GetDrawInfo()
        {
            if (!IsVisible(NodeRef))
            {
                return;
            }
            if (Owner.HostScoutDrone && _bodyModel != null)
            {
                DrawStandingBody(_bodyModel);
                return;
            }
            ModelInstance inst = _models[0];
            Model model = inst.Model;
            AnimationInfo animInfo = inst.AnimInfo;
            if (_timeSinceDamage < Owner.Values.DamageFlashTime * 2) // todo: FPS stuff
            {
                PaletteOverride = Metadata.RedPalette;
            }
            Matrix4 root = GetTransformMatrix(FacingVector, Vector3.UnitY);
            _baseNodeParent.AnimIgnoreChild = true;
            model.AnimateNodes2(index: 0, false, root, Vector3.One, animInfo);
            _baseNodeParent.AnimIgnoreChild = false;
            _baseNode.BeforeTransform = GetTransformMatrix(_aimVector, Vector3.UnitY, _baseNodeParent.Animation.Row3.Xyz);
            _baseNode.AnimIgnoreParent = true;
            model.AnimateNodes2(_baseNodeParent.ChildIndex, false, Matrix4.Identity, Vector3.One, animInfo);
            _baseNode.AnimIgnoreParent = false;
            _baseNode.BeforeTransform = null;
            root = Matrix4.CreateTranslation(Position.AddY(-0.45f));
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                Node node = model.Nodes[i];
                node.Animation *= root; // todo?: could do this in the shader
            }
            model.UpdateMatrixStack();
            UpdateMaterials(inst, Owner.BodyRecolor(inst, Recolor));
            GetDrawItems(inst, 0);
            PaletteOverride = null;
            if (_freezeTimer > 0)
            {
                _useRoomLights = true;
                float radius = 0.65f;
                var transform = Matrix4.CreateScale(radius);
                transform.Row3.Xyz = Position;
                UpdateTransforms(_altIceModel, transform, recolor: 0);
                GetDrawItems(_altIceModel, 1);
                _useRoomLights = false;
            }
        }

        // recomp: the Trooper's HUD line reads "trooper energy: %d" (owner 2026-10-08): the ROM's word, its case kept
        private static string TrooperEnergyMessage(string message)
        {
            int at = message.IndexOf("turret", StringComparison.OrdinalIgnoreCase);
            if (at < 0 || at + 6 > message.Length)
            {
                return message;
            }
            string word = Char.IsUpper(message[at]) ? (Char.IsUpper(message[at + 1]) ? "TROOPER" : "Trooper") : "trooper";
            return message[..at] + word + message[(at + 6)..];
        }

        // recomp: the Trooper's standing body (see HostBodyModel): Weavel's biped as PlayerDraw places it, facing where he
        // split; frozen = the alt ice as the turret's
        public bool HostDamageFlash => _timeSinceDamage < Owner.Values.DamageFlashTime * 2; // todo: FPS stuff

        private void DrawStandingBody(ModelInstance inst)
        {
            if (!HostBodyByApp)
            {
                Model model = inst.Model;
                model.AnimateNodes(index: 0, false, Matrix4.Identity, Vector3.One, inst.AnimInfo);
                float scale = Metadata.HunterScales[Hunter.Weavel];
                float bottom = Fixed.ToFloat(Owner.Values.MinPickupHeight);
                Vector3 facing = FacingVector.WithY(0);
                facing = facing.LengthSquared < 1e-6f ? Vector3.UnitZ : facing.Normalized();
                var right = new Vector3(facing.Z, 0, -facing.X);
                Matrix4 transform = Matrix4.Identity;
                transform.Row0.Xyz = -right * scale;
                transform.Row1.Xyz = Vector3.Cross(facing, right) * scale;
                transform.Row2.Xyz = -facing * scale;
                transform.Row3.Xyz = HostBodyPosition;
                transform.Row3.Y += bottom + bottom * (1 - scale);
                for (int i = 0; i < model.Nodes.Count; i++)
                {
                    Node node = model.Nodes[i];
                    node.Animation *= transform;
                }
                model.UpdateMatrixStack();
                if (HostDamageFlash)
                {
                    PaletteOverride = Metadata.RedPalette;
                }
                UpdateMaterials(inst, Owner.BodyRecolor(inst, Recolor));
                GetDrawItems(inst, 0);
                PaletteOverride = null;
            }
            if (_freezeTimer > 0)
            {
                _useRoomLights = true;
                var ice = Matrix4.CreateScale(0.65f);
                ice.Row3.Xyz = Position;
                UpdateTransforms(_altIceModel, ice, recolor: 0);
                GetDrawItems(_altIceModel, 1);
                _useRoomLights = false;
            }
        }

        protected override int? GetBindingOverride(ModelInstance inst, Material material, int index)
        {
            if (Owner.DoubleDamage && material.Lighting > 0)
            {
                return Owner.DoubleDmgBindingId;
            }
            return base.GetBindingOverride(inst, material, index);
        }

        protected override Vector3 GetEmission(ModelInstance inst, Material material, int index)
        {
            // todo?: it's kinda weird that this doesn't use the team emission color
            if (Owner.DoubleDamage && material.Lighting > 0)
            {
                return Metadata.EmissionGray;
            }
            return base.GetEmission(inst, material, index);
        }

        // todo: share with player
        protected override Matrix4 GetTexcoordMatrix(ModelInstance inst, Material material, int materialId,
            Node node, int recolor)
        {
            if (Owner.DoubleDamage && material.Lighting > 0 && node.BillboardMode == BillboardMode.None)
            {
                Texture texture = Owner.DoubleDamageModel.Model.Recolors[0].Textures[0];
                // product should start with the upper 3x3 of the node animation result,
                // texgenMatrix should be multiplied with the view matrix if lighting is enabled,
                // and the result should be transposed.
                // these steps are done in the shader so the view matrix can be updated when frame advance is on.
                // strictly speaking, the use_light check in the shader is not the same as what the game does,
                // since the game checks if *any* material in the model uses lighting, but the result is the same.
                Matrix4 texgenMatrix = Matrix4.Identity;
                // in-game, there's only one uniform scale factor for models
                if (inst.Model.Scale.X != 1 || inst.Model.Scale.Y != 1 || inst.Model.Scale.Z != 1)
                {
                    texgenMatrix = Matrix4.CreateScale(inst.Model.Scale) * texgenMatrix;
                }
                Matrix4 product = texgenMatrix;
                product.M12 *= -1;
                product.M13 *= -1;
                product.M22 *= -1;
                product.M23 *= -1;
                product.M32 *= -1;
                product.M33 *= -1;
                ulong frame = _scene.LiveFrames / 2;
                float rotZ = ((int)(16 * ((781874935307L * (53248 * frame) >> 32) + 2048)) >> 20) * (360 / 4096f);
                float rotY = ((int)(16 * ((781874935307L * (26624 * frame) + 0x80000000000) >> 32)) >> 20) * (360 / 4096f);
                var rot = DetMatrix.CreateRotationZ(MathHelper.DegreesToRadians(rotZ));
                rot *= DetMatrix.CreateRotationY(MathHelper.DegreesToRadians(rotY));
                product = rot * product;
                product *= 1.0f / (texture.Width / 2);
                product = new Matrix4(
                    product.Row0 * 16.0f,
                    product.Row1 * 16.0f,
                    product.Row2 * 16.0f,
                    product.Row3
                );
                return product;
            }
            return base.GetTexcoordMatrix(inst, material, materialId, node, recolor);
        }
    }
}
