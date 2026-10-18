using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Mathematics;

namespace MphRead.Entities.FirstHunt
{
    // recomp: First Hunt's Mochtroid1 (cart 0x2025e68-0x2026798; docs/FIRST_HUNT_PORT.md P2), the ancestor of MPH's
    // Petrasyl1. A copy of MphRead's Enemy03Entity with the cart's differences:
    //   - teleports between two points from the spawner's Box (rt+0x4c): +4 offset, +0x28 facing, +0x34 idle X,
    //     +0x3c idle Z, flying idle Z at 0x11e/4096 (0.0698) per frame (MphRead: 0.7)
    //   - anims: init 1, teleport in 4, active 0, teleport out 5, hurt 3 (no texcoord slot)
    //   - contact 15 (MPH 12); leaving the fly state stops all sfx
    //   - health 8, radius 0.5, no radar
    public class FhMochtroid1Entity : FhEnemyEntity
    {
        private const float MoveSpeed = 0x11E / 4096f;
        private float _idleRangeX = 0;
        private float _idleRangeZ = 0;
        private Vector3 _initialPos;
        private Vector3 _idleLimits;
        private Vector3 _field194;
        private Vector3 _field1A0;
        private float _bobAngle = 0;
        private float _bobOffset = 0;
        private float _bobSpeed = 0;
        private ushort _field170 = 0;
        private ushort _field172 = 0;
        private bool _teleportInAtInitial = true;
        private int _field18C = 0; // steps along idle Z

        public FhMochtroid1Entity(FhEnemySpawnEntity spawner, Scene scene)
            : base(EnemyType.Petrasyl1, spawner, scene)
        {
            _stateProcesses = new Action[3]
            {
                State00, State01, State02
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
            FhRawCollisionVolume box = SpawnData.Box;
            _idleRangeX = box.BoxDot1.FloatValue; // +0x34
            _idleRangeZ = box.BoxDot3.FloatValue; // +0x3c
            Vector3 facing = box.BoxVector3.ToFloatVector().WithY(0); // +0x28
            facing = facing == Vector3.Zero ? Vector3.UnitZ : facing.Normalized();
            Vector3 position = box.BoxPosition.ToFloatVector() + SpawnData.Header.Position.ToFloatVector(); // +4
            position = position.AddY(Fixed.ToFloat(5461));
            _initialPos = position;
            _idleLimits = new Vector3(
                position.X + facing.X * _idleRangeZ - facing.Z * _idleRangeX,
                position.Y,
                position.Z + facing.Z * _idleRangeZ + facing.X * _idleRangeX
            );
            facing.X *= -1;
            facing.Z *= -1;
            SetTransform(facing, Vector3.UnitY, position);
            _field194 = facing;
            _field1A0 = facing;
            _bobOffset = Fixed.ToFloat(Rng.GetRandomInt2(0x1800) + 2048) / 2; // [0.25, 1)
            _bobSpeed = Fixed.ToFloat(Rng.GetRandomInt2(0x6000)) + 1; // [1, 7)
            _field170 = _field172 = 20 * 2; // todo: FPS stuff
            UpdateState();
        }

        // 0x2026530
        private void UpdateState()
        {
            if (_state2 == 0)
            {
                // begin teleporting in, alternating between the two ends
                _models[0].SetAnimation(4, AnimFlags.NoLoop);
                if (_teleportInAtInitial)
                {
                    Position = new Vector3(_initialPos.X, Position.Y, _initialPos.Z);
                    _teleportInAtInitial = false;
                }
                else
                {
                    Position = new Vector3(_idleLimits.X, Position.Y, _idleLimits.Z);
                    _teleportInAtInitial = true;
                }
                _field194 = (-_field194).Normalized();
                SetTransform(_field194, UpVector, Position);
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
                _field18C = (int)(_idleRangeZ / MoveSpeed) * 2; // todo: FPS stuff
                _speed = (_field194 * MoveSpeed).WithY(0);
                _speed /= 2; // todo: FPS stuff
            }
            else if (_state2 == 2)
            {
                // teleport out
                _models[0].SetAnimation(5, AnimFlags.NoLoop);
                Flags |= EnemyFlags.NoHomingNc;
                Flags |= EnemyFlags.Invincible;
                _speed = Vector3.Zero;
                _field172 = 20 * 2; // todo: FPS stuff
                // FH sfx 0x2F (teleport out); MPH's stands in
                _soundSource.PlaySfx(SfxId.MOCHTROID_TELEPORT_OUT);
            }
        }

        protected override void EnemyProcess()
        {
            CallStateProcess();
        }

        private void State00()
        {
            if (CallSubroutine(_subroutines, this))
            {
                UpdateState();
            }
        }

        private void State01()
        {
            // FH sfx 0x2D (fly loop); MPH's stands in
            _soundSource.PlaySfx(SfxId.MOCHTROID_FLY, loop: true);
            _bobAngle += _bobSpeed / 2; // todo: FPS stuff
            if (_bobAngle >= 360)
            {
                _bobAngle -= 360;
            }
            float sin = MathF.Sin(MathHelper.DegreesToRadians(_bobAngle));
            _speed.Y = _initialPos.Y + sin * _bobOffset - Position.Y;
            _speed.Y /= 2; // todo: FPS stuff
            if (HitPlayers[Target.SlotIndex])
            {
                Target.TakeDamage(15, DamageFlags.None, FacingVector, this);
            }
            Vector3 between = Target.Position - Position;
            if (between.LengthSquared >= 7 * 7 || between.Xz == Vector2.Zero)
            {
                _field1A0 = _field194.WithY(0).Normalized();
            }
            else
            {
                _field1A0 = between.WithY(0).Normalized();
            }
            TurnToward(_field1A0);
            AnimationInfo animInfo = _models[0].AnimInfo;
            if (animInfo.Index[0] != 0 && animInfo.Flags[0].TestFlag(AnimFlags.Ended))
            {
                _models[0].SetAnimation(0);
            }
            if (CallSubroutine(_subroutines, this))
            {
                _soundSource.StopAllSfx();
                UpdateState();
            }
        }

        private void State02()
        {
            State00();
        }

        // facing eases 1/8 toward the wanted direction, nudged when it wouldn't move
        private void TurnToward(Vector3 wanted)
        {
            Vector3 prevFacing = FacingVector;
            Vector3 newFacing = prevFacing;
            // todo: FPS stuff
            newFacing.X += (wanted.X - prevFacing.X) / 8 / 2;
            newFacing.Z += (wanted.Z - prevFacing.Z) / 8 / 2;
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

        // teleport-out timer
        private bool Behavior00()
        {
            if (_field172 == 0)
            {
                return true;
            }
            _field172--;
            return false;
        }

        // steps along idle Z
        private bool Behavior01()
        {
            if (_field18C == 0)
            {
                return true;
            }
            _field18C--;
            return false;
        }

        // teleport-in timer
        private bool Behavior02()
        {
            if (_field170 == 0)
            {
                return true;
            }
            _field170--;
            return false;
        }

        // the cart's table (0x2073528) = Metadata.Enemy03Subroutines
        private static readonly IReadOnlyList<EnemySubroutine<FhMochtroid1Entity>> _subroutines = new EnemySubroutine<FhMochtroid1Entity>[3]
        {
            new EnemySubroutine<FhMochtroid1Entity>(new[] { new EnemyBehavior<FhMochtroid1Entity>(1, e => e.Behavior02()) }),
            new EnemySubroutine<FhMochtroid1Entity>(new[] { new EnemyBehavior<FhMochtroid1Entity>(2, e => e.Behavior01()) }),
            new EnemySubroutine<FhMochtroid1Entity>(new[] { new EnemyBehavior<FhMochtroid1Entity>(0, e => e.Behavior00()) })
        };
    }
}
