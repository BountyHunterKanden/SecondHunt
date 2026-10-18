using System;
using System.Collections.Generic;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities.FirstHunt
{
    // recomp: First Hunt's War Wasp (cart process 0x20222a4, init 0x20225b4, states 0x2073138, subroutines 0x2073120;
    // docs/FIRST_HUNT_PORT.md P2). No shipped level spawns it. It is much simpler than MPH's (MphRead's Enemy00Entity,
    // which needs MPH's movement data the cart's spawner doesn't have), so this follows the cart directly:
    //   - spawns at the spawner facing player 1, hovers there (pushed out of walls, radius 1.0), turning to watch them
    //   - state 0 (watch): after a 150-frame wait (none the first time) it dives at where the player is, 0.6 per frame
    //   - state 1 (dive): when the dive's frames run out it bounces back (X/Z reversed, a small rise) and deals 25 if
    //     it is touching the player then
    //   - state 2 (back off): 30 frames, then stops and goes back to watching
    //   - health 20 (the spawn default), hurt anim 1; the model has two anims (0 fly, 1 hurt)
    public class FhWarWaspEntity : FhEnemyEntity
    {
        private ushort _field0B4 = 0; // dive/back-off frames
        private ushort _field0B6 = 0; // wait before the next dive

        public FhWarWaspEntity(FhEnemySpawnEntity spawner, Scene scene)
            : base(EnemyType.WarWasp, spawner, scene)
        {
            _stateProcesses = new Action[3]
            {
                State0, State1, State2
            };
        }

        protected override int HurtAnimation => 1;

        protected override void EnemyInitialize()
        {
            Flags |= EnemyFlags.Visible;
            Flags &= ~EnemyFlags.NoHomingNc;
            Flags &= ~EnemyFlags.NoHomingCo;
            Flags &= ~EnemyFlags.Invincible;
            _health = _healthMax = 20;
            _boundingRadius = 1;
            Vector3 position = SpawnData.Header.Position.ToFloatVector();
            Position = position;
            FacePlayer();
            SetUpModel("warWasp", firstHunt: true);
            _field0B4 = 0;
            _field0B6 = 0;
        }

        private void FacePlayer()
        {
            Vector3 facing = Target.Position - Position;
            if (facing == Vector3.Zero)
            {
                return;
            }
            facing = facing.Normalized();
            if (MathF.Abs(facing.Y) > 0.999f)
            {
                return;
            }
            SetTransform(facing, Vector3.UnitY, Position);
        }

        protected override void EnemyProcess()
        {
            ModelInstance model = _models[0];
            if (model.AnimInfo.Flags[0].TestFlag(AnimFlags.Ended) && model.AnimInfo.Index[0] != 0)
            {
                model.SetAnimation(0);
            }
            var results = new CollisionResult[8];
            int colCount = CollisionDetection.CheckInRadius(Position, _boundingRadius, limit: 8,
                getSimpleNormal: false, TestFlags.None, _scene, results);
            for (int i = 0; i < colCount; i++)
            {
                CollisionResult result = results[i];
                Vector3 normal = result.Plane.Xyz;
                float pen;
                if (result.Field0 != 0)
                {
                    pen = _boundingRadius - result.Field14;
                }
                else
                {
                    pen = _boundingRadius + result.Plane.W - Vector3.Dot(Position, normal);
                }
                if (pen > 0)
                {
                    Position += normal * pen;
                    float dot = -Vector3.Dot(_speed, normal);
                    if (dot > 0)
                    {
                        _speed += normal * dot;
                    }
                }
            }
            // FH sfx 0x33 (wasp loop); MPH's stands in
            _soundSource.PlaySfx(SfxId.WASP_IDLE, loop: true);
            CallStateProcess();
        }

        // watch (0x2022714)
        private void State0()
        {
            FacePlayer();
            CallSubroutine(_subroutines, this);
        }

        // dive (0x20226f0)
        private void State1()
        {
            CallSubroutine(_subroutines, this);
        }

        // back off (0x20226cc)
        private void State2()
        {
            CallSubroutine(_subroutines, this);
        }

        // 0x20228c0: after the wait, dive at the player
        private bool Behavior00()
        {
            if (_field0B6 > 0)
            {
                _field0B6--;
                return false;
            }
            Vector3 between = Target.Position - Position;
            float distance = between.Length;
            _field0B4 = (ushort)(((int)(distance / 0.6f) + 1) * 2); // todo: FPS stuff
            _speed = distance == 0 ? Vector3.Zero : between * (0.6f / distance) / 2; // todo: FPS stuff
            // FH sfx 0x30 (attack); MPH's stands in
            _soundSource.PlaySfx(SfxId.WASP_ATTACK_SCR);
            return true;
        }

        // 0x20227b8: the dive ends, bounce back and sting if touching
        private bool Behavior01()
        {
            if (_field0B4 > 0)
            {
                _field0B4--;
                return false;
            }
            _field0B4 = 30 * 2; // todo: FPS stuff
            _speed.X = -_speed.X;
            _speed.Y = Fixed.ToFloat(0x7A) / 2; // todo: FPS stuff
            _speed.Z = -_speed.Z;
            float reach = _boundingRadius + Fixed.ToFloat(Target.Values.BipedColRadius);
            if (Vector3.DistanceSquared(Position, Target.Position) < reach * reach)
            {
                Target.TakeDamage(25, DamageFlags.None, FacingVector, this);
            }
            return true;
        }

        // 0x2022760: back off for 30 frames, then stop; the next dive waits 150
        private bool Behavior02()
        {
            _field0B6 = 150 * 2; // todo: FPS stuff
            if (_field0B4 > 0)
            {
                _field0B4--;
                return false;
            }
            _speed = Vector3.Zero;
            return true;
        }

        private static readonly IReadOnlyList<EnemySubroutine<FhWarWaspEntity>> _subroutines = new EnemySubroutine<FhWarWaspEntity>[3]
        {
            new EnemySubroutine<FhWarWaspEntity>(new[] { new EnemyBehavior<FhWarWaspEntity>(1, e => e.Behavior00()) }),
            new EnemySubroutine<FhWarWaspEntity>(new[] { new EnemyBehavior<FhWarWaspEntity>(2, e => e.Behavior01()) }),
            new EnemySubroutine<FhWarWaspEntity>(new[] { new EnemyBehavior<FhWarWaspEntity>(0, e => e.Behavior02()) })
        };
    }
}
