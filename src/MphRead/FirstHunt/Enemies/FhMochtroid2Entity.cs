using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Mathematics;

namespace MphRead.Entities.FirstHunt
{
    // recomp: First Hunt's Mochtroid2 (cart 0x2026798-0x20270a4; docs/FIRST_HUNT_PORT.md P2), the ancestor of MPH's
    // Petrasyl2. A copy of MphRead's Enemy04Entity with the cart's differences:
    //   - UpdateState in the cart's order (0x2027000): state 0 = teleport in (anim 6, 30 frames, invincible), state 1 =
    //     active (anim 0) for good. MphRead's Enemy04 has the two cases swapped.
    //   - circles the spawner's Cylinder (rt+0x8c): +4 offset, +0x20 weave radius; facing from the spawner header
    //   - contact 15 (MPH 12), hurt anim 3, no texcoord slot; health 8, radius 0.5, no radar
    public class FhMochtroid2Entity : FhEnemyEntity
    {
        private Vector3 _initialPos;
        private Vector3 _field188;
        private Vector3 _field194;
        private float _weaveOffset = 0;
        private float _bobAngle = 0;
        private float _bobOffset = 0;
        private float _bobSpeed = 0;
        private float _weaveAngle = 0;
        private ushort _field170 = 0;

        public FhMochtroid2Entity(FhEnemySpawnEntity spawner, Scene scene)
            : base(EnemyType.Petrasyl2, spawner, scene)
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
            SetUpModel("Mochtroid", animIndex: 0, AnimFlags.NoLoop, firstHunt: true);
            Vector3 facing = SpawnData.Header.FacingVector.ToFloatVector();
            facing = facing == Vector3.Zero ? Vector3.UnitZ : facing.Normalized();
            FhRawCollisionVolume cylinder = SpawnData.Cylinder;
            Vector3 position = cylinder.CylinderPosition.ToFloatVector() + SpawnData.Header.Position.ToFloatVector(); // +4
            position = position.AddY(Fixed.ToFloat(5461));
            SetTransform(facing, Vector3.UnitY, position);
            _initialPos = position;
            _weaveOffset = cylinder.CylinderRadius.FloatValue; // +0x20
            _field188 = facing;
            _field194 = facing;
            _bobOffset = Fixed.ToFloat(Rng.GetRandomInt2(0x1AAB) + 1365) / 2; // [0.1667, 1)
            _bobSpeed = Fixed.ToFloat(Rng.GetRandomInt2(0x6000)) + 1; // [1, 7)
            UpdateState();
        }

        // 0x2027000 (the cart's order)
        private void UpdateState()
        {
            if (_state2 == 0)
            {
                // teleport in
                _models[0].SetAnimation(6, AnimFlags.NoLoop);
                _field170 = 30 * 2; // todo: FPS stuff
                // FH sfx 0x2E (teleport in); MPH's stands in
                _soundSource.PlaySfx(SfxId.MOCHTROID_TELEPORT_IN);
            }
            else if (_state2 == 1)
            {
                // active
                _models[0].SetAnimation(0);
                Flags &= ~EnemyFlags.NoHomingNc;
                Flags &= ~EnemyFlags.Invincible;
            }
        }

        // 0x2026a9c
        private void UpdateMovement()
        {
            // FH sfx 0x2D (fly loop); MPH's stands in
            _soundSource.PlaySfx(SfxId.MOCHTROID_FLY, loop: true);
            _field188 = _spawner.Position - Position;
            _field188 = new Vector3(-_field188.Z, 0, _field188.X);
            if (_field188 != Vector3.Zero)
            {
                _field188 = _field188.Normalized();
            }
            else
            {
                _field188 = FacingVector;
            }
            int animFrame = _models[0].AnimInfo.Frame[0];
            if (_state1 != 0)
            {
                _weaveAngle += 1.5f / 2; // todo: FPS stuff
            }
            else
            {
                _weaveAngle += (1.5f * animFrame + (30 - animFrame)) / 30f / 2; // todo: FPS stuff
            }
            if (_weaveAngle >= 360)
            {
                _weaveAngle -= 360;
            }
            float angle = MathHelper.DegreesToRadians(_weaveAngle);
            float xzSin = MathF.Sin(angle);
            float xzCos = MathF.Cos(angle);
            if (_state1 != 0)
            {
                _speed.X = _initialPos.X + xzSin * _weaveOffset - Position.X;
                _speed.Z = _initialPos.Z + xzCos * _weaveOffset - Position.Z;
            }
            else
            {
                // spirals out over the 30-frame teleport-in
                _speed.X = _initialPos.X + xzSin * (_weaveOffset * animFrame / 30) - Position.X;
                _speed.Z = _initialPos.Z + xzCos * (_weaveOffset * animFrame / 30) - Position.Z;
            }
            _bobAngle += _bobSpeed / 2; // todo: FPS stuff
            if (_bobAngle >= 360)
            {
                _bobAngle -= 360;
            }
            float ySin = MathF.Sin(MathHelper.DegreesToRadians(_bobAngle));
            _speed.Y = _initialPos.Y + ySin * _bobOffset - Position.Y;
            _speed /= 2; // todo: FPS stuff
            Vector3 between = Target.Position - Position;
            if (between.LengthSquared >= 7 * 7 || between.Xz == Vector2.Zero)
            {
                _field194 = _field188.WithY(0).Normalized();
            }
            else
            {
                _field194 = between.WithY(0).Normalized();
            }
            Vector3 prevFacing = FacingVector;
            Vector3 newFacing = prevFacing;
            // todo: FPS stuff
            newFacing.X += (_field194.X - prevFacing.X) / 8 / 2;
            newFacing.Z += (_field194.Z - prevFacing.Z) / 8 / 2;
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
        }

        protected override void EnemyProcess()
        {
            CallStateProcess();
        }

        // teleporting in (0x2026a64)
        private void State0()
        {
            UpdateMovement();
            if (CallSubroutine(_subroutines, this))
            {
                UpdateState();
            }
        }

        // active (0x202697c)
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

        // the cart's table (0x2073640) = Metadata.Enemy04Subroutines
        private static readonly IReadOnlyList<EnemySubroutine<FhMochtroid2Entity>> _subroutines = new EnemySubroutine<FhMochtroid2Entity>[2]
        {
            new EnemySubroutine<FhMochtroid2Entity>(new[] { new EnemyBehavior<FhMochtroid2Entity>(1, e => e.Behavior01()) }),
            new EnemySubroutine<FhMochtroid2Entity>(new[] { new EnemyBehavior<FhMochtroid2Entity>(1, e => e.Behavior00()) })
        };
    }
}
