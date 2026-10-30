using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Mathematics;

namespace MphRead.Entities.FirstHunt
{
    // recomp: First Hunt's Mochtroid4 (cart 0x2027b28-0x202806c, helpers to 0x2028b90; docs/FIRST_HUNT_PORT.md P2), the
    // ancestor of MPH's Petrasyl4: it teleports out when the player comes within 6 and back in when they leave. A copy
    // of MphRead's Enemy06Entity with the cart's differences:
    //   - drifts inside the spawner's Cylinder (rt+0x8c): +4 offset, +0x1c height, +0x20 XZ radius
    //   - the separation check walks the other Mochtroid4s (MphRead's Enemy06 looks for Petrasyl3s) and uses the 0.5
    //     bounding spheres
    //   - the near check doesn't stop the fly sfx; leaving the fly state stops all sfx instead
    //   - anims: init 1, teleport in 8, active 0, teleport out 9, hidden 0 (no loop), hurt 3 (no texcoord slot)
    //   - contact 15 (MPH 12); health 8, radius 0.5, no radar
    //   - todo: entering state 2 also clears the player's lock-on/target (player +0x330/+0x328) when it is this enemy
    public class FhMochtroid4Entity : FhEnemyEntity
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
        private ushort _field172 = 0;
        private float _targetY = 0;
        private ushort _field1A0 = 0;

        public FhMochtroid4Entity(FhEnemySpawnEntity spawner, Scene scene)
            : base(EnemyType.Petrasyl4, spawner, scene)
        {
            _stateProcesses = new Action[5]
            {
                State0, State1, State2, State3, State4
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
            _field170 = 10 * 2; // todo: FPS stuff
            _field172 = 10 * 2; // todo: FPS stuff
            UpdateState();
        }

        // 0x2028658
        private void UpdateState()
        {
            bool updateSpeed = false;
            if (_state2 == 0)
            {
                Flags |= EnemyFlags.Visible;
                _models[0].SetAnimation(8, AnimFlags.NoLoop);
                _field170 = 10 * 2; // todo: FPS stuff
                // FH sfx 0x2E (teleport in); MPH's stands in
                _soundSource.PlaySfx(SfxId.MOCHTROID_TELEPORT_IN);
            }
            else if (_state2 == 1)
            {
                Flags &= ~EnemyFlags.Invincible;
                Flags &= ~EnemyFlags.NoHomingNc;
                _models[0].SetAnimation(0);
                if (_state1 == 0)
                {
                    updateSpeed = true;
                }
            }
            else if (_state2 == 2)
            {
                Flags |= EnemyFlags.Invincible;
                Flags |= EnemyFlags.NoHomingNc;
                _models[0].SetAnimation(9, AnimFlags.NoLoop);
                if (_state1 == 1)
                {
                    _field172 = 10 * 2; // todo: FPS stuff
                }
                else
                {
                    _field172 = (ushort)(10 * 2 - _field170); // todo: FPS stuff
                    _models[0].AnimInfo.Frame[0] = (10 * (10 - _field172 / 2) - 1) / 10; // todo: FPS stuff
                    if (_state1 == 0)
                    {
                        updateSpeed = true;
                    }
                }
                // FH sfx 0x2F (teleport out); MPH's stands in
                _soundSource.PlaySfx(SfxId.MOCHTROID_TELEPORT_OUT);
            }
            else if (_state2 == 3)
            {
                Flags &= ~EnemyFlags.Visible;
                _models[0].SetAnimation(0, AnimFlags.NoLoop);
            }
            else if (_state2 == 4)
            {
                Flags |= EnemyFlags.Visible;
                _models[0].SetAnimation(8, AnimFlags.NoLoop);
                if (_state1 == 3)
                {
                    _field170 = 10 * 2; // todo: FPS stuff
                }
                else
                {
                    _field170 = (ushort)(10 * 2 - _field172); // todo: FPS stuff
                    _models[0].AnimInfo.Frame[0] = (10 * (10 - _field170 / 2) - 1) / 10; // todo: FPS stuff
                }
                _soundSource.PlaySfx(SfxId.MOCHTROID_TELEPORT_IN);
            }
            if (updateSpeed)
            {
                // the cart sets the RNG results in _field184 but immediately overwrites them
                Rng.GetRandomInt2(0x1000);
                Rng.GetRandomInt2(0x1000);
                Rng.GetRandomInt2(0x1000);
                Vector3 facing = FacingVector;
                _field184 = facing * 0.05f;
                if (_field184.X == 0 && _field184.Y == 0)
                {
                    _field184 = facing;
                }
                else
                {
                    _field184 = _field184.Normalized();
                }
                _speed = _field184 * 0.05f;
                _speed /= 2; // todo: FPS stuff
            }
        }

        // the cart's separation: another Mochtroid4 whose bounding sphere overlaps this one
        private FhMochtroid4Entity? FindOverlapping()
        {
            foreach (EnemyInstanceEntity enemy in _scene.GetEnemyInstanceEntities())
            {
                if (enemy == this || enemy is not FhMochtroid4Entity other || other.Health == 0)
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

        // 0x202806c: Mochtroid3's movement, with the separation only while visible
        private void UpdateMovement()
        {
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
                            if (Flags.TestFlag(EnemyFlags.Visible))
                            {
                                FhMochtroid4Entity? other = FindOverlapping();
                                if (other != null)
                                {
                                    _field184 = Position - other.Position;
                                    _field1A0 = 5 * 2; // todo: FPS stuff
                                }
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
            // the cart compares against -0x70000 here, so it never faces the player (same as MphRead's Enemy06)
            _field190 = _field184.Xz == Vector2.Zero ? prevFacing : _field184.WithY(0).Normalized();
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

        // teleporting in, in place
        private void State0()
        {
            _models[0].AnimInfo.Frame[0] = (10 * (10 - _field170 / 2) - 1) / 10; // todo: FPS stuff
            if (CallSubroutine(_subroutines, this))
            {
                UpdateState();
            }
        }

        // active
        private void State1()
        {
            // FH sfx 0x2D (fly loop, played in the movement while state 1); MPH's stands in
            _soundSource.PlaySfx(SfxId.MOCHTROID_FLY, loop: true);
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
            if (CallSubroutine(_subroutines, this))
            {
                _soundSource.StopAllSfx();
                UpdateState();
            }
        }

        // teleporting out
        private void State2()
        {
            UpdateMovement();
            if (CallSubroutine(_subroutines, this))
            {
                UpdateState();
            }
        }

        // hidden
        private void State3()
        {
            State2();
        }

        // teleporting back in
        private void State4()
        {
            _models[0].AnimInfo.Frame[0] = (10 * (10 - _field170 / 2) - 1) / 10; // todo: FPS stuff
            UpdateMovement();
            if (CallSubroutine(_subroutines, this))
            {
                UpdateState();
            }
        }

        // the player is within 6 (0x2027fa4; unlike MphRead's, it doesn't stop the fly sfx)
        private bool Behavior00()
        {
            Vector3 between = Target.Position - Position;
            return between.LengthSquared < 6 * 6;
        }

        private bool Behavior01()
        {
            return !Behavior00();
        }

        private bool Behavior02()
        {
            if (_field170 == 0)
            {
                return true;
            }
            _field170--;
            return false;
        }

        private bool Behavior03()
        {
            if (_field172 == 0)
            {
                return true;
            }
            _field172--;
            return false;
        }

        // the cart's table (0x2073890) = Metadata.Enemy06Subroutines
        private static readonly IReadOnlyList<EnemySubroutine<FhMochtroid4Entity>> _subroutines = new EnemySubroutine<FhMochtroid4Entity>[5]
        {
            // state 0: timer A -> 1, near -> 2
            new EnemySubroutine<FhMochtroid4Entity>(new[]
            {
                new EnemyBehavior<FhMochtroid4Entity>(1, e => e.Behavior02()),
                new EnemyBehavior<FhMochtroid4Entity>(2, e => e.Behavior00())
            }),
            // state 1: near -> 2
            new EnemySubroutine<FhMochtroid4Entity>(new[]
            {
                new EnemyBehavior<FhMochtroid4Entity>(2, e => e.Behavior00())
            }),
            // state 2: timer B -> 3, far -> 4
            new EnemySubroutine<FhMochtroid4Entity>(new[]
            {
                new EnemyBehavior<FhMochtroid4Entity>(3, e => e.Behavior03()),
                new EnemyBehavior<FhMochtroid4Entity>(4, e => e.Behavior01())
            }),
            // state 3: far -> 4
            new EnemySubroutine<FhMochtroid4Entity>(new[]
            {
                new EnemyBehavior<FhMochtroid4Entity>(4, e => e.Behavior01())
            }),
            // state 4: timer A -> 1, near -> 2
            new EnemySubroutine<FhMochtroid4Entity>(new[]
            {
                new EnemyBehavior<FhMochtroid4Entity>(1, e => e.Behavior02()),
                new EnemyBehavior<FhMochtroid4Entity>(2, e => e.Behavior00())
            })
        };
    }
}
