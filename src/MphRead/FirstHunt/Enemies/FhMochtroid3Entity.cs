using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Mathematics;

namespace MphRead.Entities.FirstHunt
{
    // recomp: First Hunt's Mochtroid3 (cart 0x20270a4-0x2027b28; docs/FIRST_HUNT_PORT.md P2), the ancestor of MPH's
    // Petrasyl3. A copy of MphRead's Enemy05Entity with the cart's differences:
    //   - drifts inside the spawner's Cylinder (rt+0x8c): +4 offset, +0x1c height, +0x20 XZ radius
    //   - the separation check walks the other Mochtroid3s and uses the 0.5 bounding spheres (MPH: hurt volumes)
    //   - anims: init 1, teleport in 7, active 0, hurt 3 (no texcoord slot)
    //   - contact 15 (MPH 12); health 8, radius 0.5, no radar
    public class FhMochtroid3Entity : FhEnemyEntity
    {
        private Vector3 _initialPos;
        private Vector3 _field184;
        private Vector3 _field190;
        private float _weaveOffset = 0;
        private float _field1B0 = 0; // height of the drift cylinder
        private float _bobAngle = 0;
        private float _bobOffset = 0;
        private float _bobSpeed = 0;
        private ushort _field170 = 0;
        private float _targetY = 0;
        private ushort _field1A0 = 0;

        public FhMochtroid3Entity(FhEnemySpawnEntity spawner, Scene scene)
            : base(EnemyType.Petrasyl3, spawner, scene)
        {
            _stateProcesses = new Action[2]
            {
                State0, State1
            };
        }

        protected override int HurtAnimation => 3;

        protected override void EnemyInitialize()
        {
            _health = _healthMax = 8;
            Flags |= EnemyFlags.Visible;
            Flags |= EnemyFlags.NoHomingNc;
            Flags |= EnemyFlags.Invincible;
            _boundingRadius = 0.5f;
            SetUpModel("Mochtroid", animIndex: 1, AnimFlags.NoLoop, firstHunt: true);
            Vector3 facing = SpawnData.Header.FacingVector.ToFloatVector();
            facing = facing == Vector3.Zero ? Vector3.UnitZ : facing.Normalized();
            FhRawCollisionVolume cylinder = SpawnData.Cylinder;
            Vector3 position = cylinder.CylinderPosition.ToFloatVector() + SpawnData.Header.Position.ToFloatVector(); // +4
            position = position.AddY(Fixed.ToFloat(5461));
            SetTransform(facing, Vector3.UnitY, position);
            _initialPos = position;
            _weaveOffset = cylinder.CylinderRadius.FloatValue; // +0x20
            _field1B0 = cylinder.CylinderDot.FloatValue; // +0x1c
            _field184 = facing;
            _field190 = facing;
            _bobOffset = Fixed.ToFloat(Rng.GetRandomInt2(0x1AAB) + 1365) / 2; // [0.1667, 1)
            _bobSpeed = Fixed.ToFloat(Rng.GetRandomInt2(0x3000)) + 1; // [1, 4)
            _targetY = Position.Y;
            _field170 = 20 * 2; // todo: FPS stuff
            UpdateState();
        }

        // 0x20279a0
        private void UpdateState()
        {
            if (_state2 == 0)
            {
                // begin teleporting in
                _models[0].SetAnimation(7, AnimFlags.NoLoop);
                _field170 = 20 * 2; // todo: FPS stuff
                // FH sfx 0x2E (teleport in); MPH's stands in
                _soundSource.PlaySfx(SfxId.MOCHTROID_TELEPORT_IN);
            }
            else if (_state2 == 1)
            {
                // finish teleporting in
                _models[0].SetAnimation(0);
                Flags &= ~EnemyFlags.NoHomingNc;
                Flags &= ~EnemyFlags.Invincible;
                _field184 = new Vector3(
                    Fixed.ToFloat(Rng.GetRandomInt2(4096) - 2048), // [-0.5, 0.5)
                    Fixed.ToFloat(Rng.GetRandomInt2(4096) - 2048),
                    Fixed.ToFloat(Rng.GetRandomInt2(4096) - 2048)
                );
                if (_field184.X == 0 && _field184.Z == 0)
                {
                    _field184 = FacingVector;
                }
                else
                {
                    _field184 = _field184.Normalized();
                }
                _speed = _field184 * 0.05f;
                _speed /= 2; // todo: FPS stuff
            }
        }

        // the cart's separation (0x20203d4(0xf) walk): another Mochtroid3 whose bounding sphere overlaps this one
        private FhMochtroid3Entity? FindOverlapping()
        {
            foreach (EnemyInstanceEntity enemy in _scene.GetEnemyInstanceEntities())
            {
                if (enemy == this || enemy is not FhMochtroid3Entity other || other.Health == 0)
                {
                    continue;
                }
                float reach = _boundingRadius + other._boundingRadius;
                if (Vector3.DistanceSquared(Position, other.Position) < reach * reach)
                {
                    return other;
                }
            }
            return null;
        }

        private void UpdateMovement()
        {
            // FH sfx 0x2D (fly loop); MPH's stands in
            _soundSource.PlaySfx(SfxId.MOCHTROID_FLY, loop: true);
            var toTarget = new Vector3(Position.X - _initialPos.X, _targetY - _initialPos.Y, Position.Z - _initialPos.Z);
            _bobAngle += _bobSpeed / 2; // todo: FPS stuff
            if (_bobAngle >= 360)
            {
                _targetY = Position.Y;
                _bobAngle -= 360;
            }
            float ySin = MathF.Sin(MathHelper.DegreesToRadians(_bobAngle));
            float ySpeedInc = _targetY + ySin * _bobOffset - Position.Y;
            if (_field1A0 > 0)
            {
                _field1A0--;
            }
            if (_field1A0 == 0)
            {
                if (_targetY >= _initialPos.Y)
                {
                    if (_targetY <= _initialPos.Y + _field1B0)
                    {
                        if (toTarget.X * toTarget.X + toTarget.Z * toTarget.Z <= _weaveOffset * _weaveOffset)
                        {
                            FhMochtroid3Entity? other = FindOverlapping();
                            if (other != null)
                            {
                                _field184 = Position - other.Position;
                                _field1A0 = 5 * 2; // todo: FPS stuff
                            }
                        }
                        else
                        {
                            _field184.X *= -1;
                            _field184.Y = Fixed.ToFloat(Rng.GetRandomInt2(0x1000)) - 0.5f - (toTarget.Y - _field1B0 / 2) / _field1B0;
                            _field184.Z *= -1;
                            _field1A0 = 5 * 2; // todo: FPS stuff
                        }
                    }
                    else
                    {
                        _field184.X = Fixed.ToFloat(Rng.GetRandomInt2(0x1000)) - 0.5f - toTarget.X / _weaveOffset;
                        _field184.Y = -Fixed.ToFloat(Rng.GetRandomInt2(0x800)) - 0.5f;
                        _field184.Z = Fixed.ToFloat(Rng.GetRandomInt2(0x1000)) - 0.5f - toTarget.Z / _weaveOffset;
                        _field1A0 = 5 * 2; // todo: FPS stuff
                    }
                }
                else
                {
                    _field184.X = Fixed.ToFloat(Rng.GetRandomInt2(0x1000)) - 0.5f - toTarget.X / _weaveOffset;
                    _field184.Y = Fixed.ToFloat(Rng.GetRandomInt2(0x800)) + 0.5f;
                    _field184.Z = Fixed.ToFloat(Rng.GetRandomInt2(0x1000)) - 0.5f - toTarget.Z / _weaveOffset;
                    _field1A0 = 5 * 2; // todo: FPS stuff
                }
            }
            Vector3 prevFacing = FacingVector;
            if (_field184.X == 0 && _field184.Y == 0 || Single.IsNaN(_field184.X))
            {
                _field184 = prevFacing;
            }
            else
            {
                _field184 = _field184.Normalized();
            }
            Vector3 between = Target.Position - Position;
            if (between.LengthSquared >= 2 * 2 || between.Xz == Vector2.Zero)
            {
                _field190 = _field184.Xz == Vector2.Zero ? prevFacing : _field184.WithY(0).Normalized();
            }
            else
            {
                _field190 = between.WithY(0).Normalized();
            }
            Vector3 newFacing = prevFacing;
            // todo: FPS stuff
            newFacing.X += (_field190.X - prevFacing.X) / 8 / 2;
            newFacing.Z += (_field190.Z - prevFacing.Z) / 8 / 2;
            if (newFacing.X == 0 && newFacing.Z == 0)
            {
                newFacing = prevFacing;
            }
            Debug.Assert(newFacing != Vector3.Zero);
            newFacing = newFacing.Normalized();
            if (MathF.Abs(newFacing.X - prevFacing.X) < 1 / 4096f && MathF.Abs(newFacing.Z - prevFacing.Z) < 1 / 4096f)
            {
                newFacing.X += 0.125f / 2;
                newFacing.Z -= 0.125f / 2;
                if (newFacing.X == 0 && newFacing.Z == 0)
                {
                    newFacing.X += 0.125f / 2;
                    newFacing.Z -= 0.125f / 2;
                }
                newFacing = newFacing.Normalized();
            }
            SetTransform(newFacing, UpVector, Position);
            // todo: FPS stuff
            _speed = _field184 * 0.05f;
            _speed /= 2;
            _targetY += _speed.Y / 2;
            _speed.Y += ySpeedInc / 2;
        }

        protected override void EnemyProcess()
        {
            CallStateProcess();
        }

        // teleporting in (0x202793c)
        private void State0()
        {
            if (CallSubroutine(_subroutines, this))
            {
                UpdateState();
            }
        }

        // active (0x20272ac)
        private void State1()
        {
            UpdateMovement();
            if (HitPlayers[Target.SlotIndex])
            {
                Target.TakeDamage(15, DamageFlags.None, FacingVector, this);
            }
            AnimationInfo animInfo = _models[0].AnimInfo;
            if (animInfo.Index[0] != 0 && animInfo.Flags[0].TestFlag(AnimFlags.Ended))
            {
                _models[0].SetAnimation(0);
            }
        }

        private bool Behavior00()
        {
            return false;
        }

        private bool Behavior01()
        {
            if (_field170 == 0)
            {
                return true;
            }
            _field170--;
            return false;
        }

        // the cart's table (0x2073750) = Metadata.Enemy05Subroutines
        private static readonly IReadOnlyList<EnemySubroutine<FhMochtroid3Entity>> _subroutines = new EnemySubroutine<FhMochtroid3Entity>[2]
        {
            new EnemySubroutine<FhMochtroid3Entity>(new[] { new EnemyBehavior<FhMochtroid3Entity>(1, e => e.Behavior01()) }),
            new EnemySubroutine<FhMochtroid3Entity>(new[] { new EnemyBehavior<FhMochtroid3Entity>(1, e => e.Behavior00()) })
        };
    }
}
