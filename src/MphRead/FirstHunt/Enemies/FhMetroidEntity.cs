using System;
using System.Collections.Generic;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities.FirstHunt
{
    // recomp: First Hunt's Metroid (cart process 0x2023a1c, init 0x2023fe4, SetState 0x2025884, states 0x2073420,
    // subroutines 0x20733c8; docs/FIRST_HUNT_PORT.md P2), the ancestor of MPH's Temroid. A copy of MphRead's
    // Enemy02Entity with the cart's differences:
    //   - health 50 (hard-coded), radius 1.0, contact 15, no hurt animation
    //   - placement/patrol from the spawner's Box (rt+0x4c): +4 offset, +0x28 facing, +0x34 idle X, +0x3c idle Z
    //   - Behavior10 (give up the chase) is dist^2 > 100 (MphRead: < 100)
    //   - the rise/lunge height uses a real degree sine (MphRead feeds degrees to MathF.Sin)
    //   - the latch drains 2 every 9th DS frame, comes off after 150 frames, a bomb or the player's death, and stays
    //     on through morphing (it re-poses on the ball); only one per player (player.AttachedEnemy)
    //   - sfx 0x3E while recoiling (state 2) and 0x41 while turning (Func216469C); MPH's METROID_CHARGE/FLY stand in
    public class FhMetroidEntity : FhEnemyEntity
    {
        private readonly Vector3[] _idlePoints = new Vector3[4];
        private int _field170 = 0;
        private byte _field1A4 = 0;
        private byte _drainDamageTimer = 0;
        private float _field1B8 = 0; // angle in degrees
        private Vector3 _field1BC;
        private bool _field1D0 = false; // chasing (counts toward the cap of 3)
        private bool _hitByBomb = false;

        public bool Field1D0 => _field1D0;

        public FhMetroidEntity(FhEnemySpawnEntity spawner, Scene scene)
            : base(EnemyType.Temroid, spawner, scene)
        {
            _stateProcesses = new Action[11]
            {
                State00, State01, State02, State03, State04, State05, State06, State07, State08, State09, State10
            };
        }

        protected override void EnemyInitialize()
        {
            Flags |= EnemyFlags.Visible;
            Flags &= ~EnemyFlags.NoHomingNc;
            Flags &= ~EnemyFlags.NoHomingCo;
            Flags &= ~EnemyFlags.Invincible;
            _health = _healthMax = 50;
            FhRawCollisionVolume box = SpawnData.Box;
            Vector3 facing = box.BoxVector3.ToFloatVector().WithY(0); // +0x28
            facing = facing == Vector3.Zero ? Vector3.UnitZ : facing.Normalized();
            SetTransform(facing, Vector3.UnitY,
                SpawnData.Header.Position.ToFloatVector() + box.BoxPosition.ToFloatVector().AddY(0.5f));
            _boundingRadius = 1;
            SetUpModel("metroid", firstHunt: true);
            _idlePoints[0] = Position;
            _idlePoints[1] = Position;
            _idlePoints[2] = Position;
            _idlePoints[3] = Position;
            _field1BC = Position;
            float idleX = box.BoxDot1.FloatValue; // +0x34
            float idleZ = box.BoxDot3.FloatValue; // +0x3c
            _idlePoints[1].X += facing.X * idleZ;
            _idlePoints[1].Z += facing.Z * idleZ;
            _idlePoints[2].X += facing.X * idleZ - facing.Z * idleX;
            _idlePoints[2].Z += facing.Z * idleZ + facing.X * idleX;
            _idlePoints[3].X -= facing.Z * idleX;
            _idlePoints[3].Z += facing.X * idleX;
            Func21648A4();
        }

        // SetState (0x2025884)
        private void Func21648A4()
        {
            if ((_state1 == 0 || _state1 == 7 || _state1 == 10) && _state2 != 0 && _state2 != 7 && _state2 != 10)
            {
                int count = 0;
                foreach (EnemyInstanceEntity enemy in _scene.GetEnemyInstanceEntities())
                {
                    if (enemy is FhMetroidEntity metroid && metroid.Field1D0)
                    {
                        count++;
                    }
                }
                if (count >= 3)
                {
                    _state2 = _state1;
                    return;
                }
                _field1D0 = true;
            }
            else if (_state1 != 0 && _state1 != 7 && _state1 != 10 && (_state2 == 0 || _state2 == 7 || _state2 == 10))
            {
                _field1D0 = false;
            }
            if (_state2 == 0)
            {
                _models[0].SetAnimation(0);
                _speed = Vector3.Zero;
            }
            else if (_state2 == 1)
            {
                _models[0].SetAnimation(1);
            }
            else if (_state2 == 2)
            {
                _soundSource.StopAllSfx();
                _models[0].SetAnimation(10, AnimFlags.NoLoop);
                _speed = Vector3.Zero;
            }
            else if (_state2 == 3)
            {
                _models[0].SetAnimation(11, AnimFlags.NoLoop);
                _field1B8 = 0;
                _field1BC = Position;
                Vector3 facing = (Target.Position - Position).AddY(0.5f).Normalized();
                SetFacing(facing);
                _speed = -(facing * 0.3f).WithY(0) / 2; // todo: FPS stuff
            }
            else if (_state2 == 4)
            {
                _models[0].SetAnimation(12, AnimFlags.NoLoop);
                Vector3 facing = (Target.Position - Position).AddY(0.5f).Normalized();
                _speed = (facing * 0.3f).WithY(0) / 2; // todo: FPS stuff
            }
            else if (_state2 == 5)
            {
                _models[0].SetAnimation(3);
                Vector3 facing = (Target.Position - Position).AddY(0.5f).Normalized();
                _speed = facing / 2 / 2; // todo: FPS stuff
                _field170 = 20 * 2; // todo: FPS stuff
            }
            else if (_state2 == 6)
            {
                if (_models[0].AnimInfo.Index[0] == 7)
                {
                    _models[0].SetAnimation(9, AnimFlags.NoLoop);
                }
                else
                {
                    _models[0].SetAnimation(8, AnimFlags.NoLoop);
                }
            }
            else if (_state2 == 7)
            {
                _models[0].SetAnimation(0);
            }
            else if (_state2 == 8)
            {
                Vector3 facing;
                Vector3 playerFacing = Target.FacingVector;
                if (Target.IsAltForm)
                {
                    _models[0].SetAnimation(7);
                    facing = -playerFacing.WithY(0);
                    if (facing.X == 0 && facing.Z == 0)
                    {
                        facing.X = 1;
                    }
                }
                else
                {
                    _models[0].SetAnimation(4);
                    facing = -playerFacing;
                }
                SetFacing(facing.Normalized());
                _speed = Vector3.Zero;
                _hitByBomb = false;
                _field170 = 150 * 2; // todo: FPS stuff
                _drainDamageTimer = 0;
            }
            else if (_state2 == 9)
            {
                _models[0].SetAnimation(15, AnimFlags.NoLoop);
                _speed = Vector3.Zero;
            }
            else if (_state2 == 10)
            {
                _models[0].SetAnimation(2);
                _speed = Vector3.Zero;
            }
        }

        // keeps the transform valid when the wanted facing is (close to) vertical
        private void SetFacing(Vector3 facing, Vector3? position = null)
        {
            if (Single.IsNaN(facing.X) || facing == Vector3.Zero || MathF.Abs(facing.Y) > 0.999f)
            {
                facing = FacingVector;
            }
            SetTransform(facing, UpVector, position ?? Position);
        }

        private static bool FloatEqual(float a, float b)
        {
            return MathF.Abs(a - b) >= 1 / 4096f;
        }

        // 0x2025654: turn toward point1 - point2 and move along the facing
        private void Func216469C(Vector3 point1, Vector3 point2, float sign)
        {
            // FH sfx 0x41 (Metroid move loop); MPH's METROID_FLY stands in
            _soundSource.PlaySfx(SfxId.METROID_FLY, loop: true);
            Vector3 between = (point1 - point2).Normalized();
            if (Single.IsNaN(between.X))
            {
                return;
            }
            Vector3 facing = FacingVector;
            if (FloatEqual(facing.X, between.X) || FloatEqual(facing.Y, between.Y) || FloatEqual(facing.Z, between.Z))
            {
                Vector3 prevFacing = facing;
                facing.X += (between.X - facing.X) / 8; // sktodo: FPS?
                facing.Z += (between.Z - facing.Z) / 8;
                if (facing.X == 0 && facing.Z == 0)
                {
                    facing.X = prevFacing.X;
                    facing.Z = prevFacing.Z;
                }
                facing.Y = between.Y;
                facing = facing.Normalized();
                if (FloatEqual(facing.X, prevFacing.X) && FloatEqual(facing.Z, prevFacing.Z))
                {
                    facing.X += 0.125f; // sktodo: FPS?
                    facing.Z -= 0.125f;
                    if (facing.X == 0 && facing.Z == 0)
                    {
                        facing.X += 0.125f;
                        facing.Z -= 0.125f;
                    }
                    facing = facing.Normalized();
                }
                _speed = sign * (facing * 0.1f / 2); // todo: FPS stuff
                facing.Y = 0;
                if (facing.X != 0 || facing.Z != 0)
                {
                    SetFacing(facing.Normalized());
                }
            }
        }

        // 0x20239f0: the player lets go (death, or leaving the latch)
        protected override void Detach()
        {
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                if (player.AttachedEnemy == this)
                {
                    player.AttachedEnemy = null;
                }
            }
            _field1D0 = false;
        }

        protected override void OnDeath()
        {
            Detach();
        }

        public override void Destroy()
        {
            Detach(); // (left behind or removed while latched)
            base.Destroy();
        }

        // called by the player each frame while latched in biped form (PlayerProcess, like Enemy02Entity's)
        public void UpdateAttached(PlayerEntity player)
        {
            Vector3 position = player.CameraInfo.Position + player.FacingVector / 2;
            SetFacing(-player.FacingVector, position);
        }

        protected override void EnemyProcess()
        {
            CallStateProcess();
            if (_state1 != 8)
            {
                foreach (DoorEntity door in _scene.GetDoorEntities())
                {
                    Vector3 doorFacing = door.FacingVector;
                    Vector3 between = Position - door.LockPosition;
                    float magSqr = between.LengthSquared;
                    if (magSqr < door.RadiusSquared + _boundingRadius)
                    {
                        float dist = door.RadiusSquared + _boundingRadius - magSqr;
                        if (dist > 0)
                        {
                            if (Vector3.Dot(between, doorFacing) < 0)
                            {
                                dist *= -1;
                            }
                            Position += doorFacing * dist;
                            PushOff(doorFacing);
                        }
                    }
                }
                var results = new CollisionResult[8];
                int colCount = CollisionDetection.CheckInRadius(Position, _boundingRadius, limit: 8,
                    getSimpleNormal: false, TestFlags.None, _scene, results);
                for (int i = 0; i < colCount; i++)
                {
                    CollisionResult result = results[i];
                    float dist;
                    if (result.Field0 == 0)
                    {
                        dist = _boundingRadius + result.Plane.W - Vector3.Dot(Position, result.Plane.Xyz);
                    }
                    else
                    {
                        dist = _boundingRadius - result.Field14;
                    }
                    if (dist > 0)
                    {
                        Position += result.Plane.Xyz * dist;
                        PushOff(result.Plane.Xyz);
                    }
                }
            }
        }

        // drop the speed going into a surface (MphRead computes this into a local and drops it); a dash that hits
        // something ends
        private void PushOff(Vector3 normal)
        {
            float dot = -Vector3.Dot(_speed, normal);
            if (dot > 0)
            {
                _speed += normal * dot;
                if (_state2 == 5)
                {
                    _field170 = 0;
                }
            }
        }

        // the latch's bomb check (MphRead's CheckTemroidHitByBomb): a bomb going off next to the latched Metroid
        // shakes it off (Behavior07). Named apart from EnemyInstanceEntity.CheckHitByBomb, which deals bomb damage.
        public bool CheckMetroidHitByBomb(BombEntity bomb)
        {
            if (Flags.TestFlag(EnemyFlags.Invincible) || !bomb.Flags.TestFlag(BombFlags.Exploding) || bomb.Flags.TestFlag(BombFlags.Exploded))
            {
                return false;
            }
            Vector3 between = Position - bomb.Position;
            if (between.LengthSquared > bomb.Radius * bomb.Radius)
            {
                return false;
            }
            _hitByBomb = true;
            return true;
        }

        private void State00()
        {
            Vector3 between = _idlePoints[_field1A4] - Position;
            if (between.LengthSquared < 1 && ++_field1A4 >= 4)
            {
                _field1A4 = 0;
            }
            Func216469C(_idlePoints[_field1A4], Position, sign: 1);
            State02();
        }

        private void State01()
        {
            Func216469C(Target.Position, Position, sign: 1);
            State02();
        }

        private void State02()
        {
            if (_state1 == 2)
            {
                // FH sfx 0x3E (state 2 loop); MPH's METROID_CHARGE stands in
                _soundSource.PlaySfx(SfxId.METROID_CHARGE, loop: true);
            }
            ContactDamage();
            if (CallSubroutine(_subroutines, this))
            {
                Func21648A4();
            }
        }

        private void ContactDamage()
        {
            if (HitPlayers[Target.SlotIndex])
            {
                Target.TakeDamage(15, DamageFlags.None, direction: null, this);
            }
        }

        private void UpdateHeight(float sign)
        {
            _field1B8 += sign * (45f / _models[0].AnimInfo.FrameCount[0] / 2); // todo: FPS stuff
            if (_field1B8 >= 360)
            {
                _field1B8 = 0;
            }
            // the cart reads its degree sine table here; MphRead's Enemy02 passes degrees to MathF.Sin
            _speed.Y = _field1BC.Y + MathF.Sin(MathHelper.DegreesToRadians(_field1B8)) / 2 - Position.Y;
        }

        private void State03()
        {
            UpdateHeight(sign: 1);
            State02();
        }

        private void State04()
        {
            UpdateHeight(sign: -1);
            State02();
        }

        private void State05()
        {
            if (CallSubroutine(_subroutines, this))
            {
                Func21648A4();
            }
        }

        private void State06()
        {
            Func216469C(Position, _idlePoints[0], sign: -1);
            if (CallSubroutine(_subroutines, this))
            {
                Func21648A4();
            }
        }

        private void State07()
        {
            Func216469C(_idlePoints[0], Position, sign: 1);
            State02();
        }

        private void State08()
        {
            PlayerEntity player = Target;
            int animId = _models[0].AnimInfo.Index[0];
            Vector3 facing = FacingVector;
            Vector3 playerPos = player.Position;
            Vector3 playerFacing = player.FacingVector;
            if (player.IsMorphing)
            {
                if (facing.Y != 0)
                {
                    if (facing.X == 0 && facing.Z == 0)
                    {
                        facing.X = 1;
                    }
                }
                if (animId == 4 || animId == 14)
                {
                    _models[0].SetAnimation(13, AnimFlags.NoLoop);
                }
                SetFacing(facing.Normalized(), playerPos.AddY(0.625f));
            }
            else if (player.IsUnmorphing)
            {
                if (facing.Y == 0)
                {
                    facing.Y = -playerFacing.Y;
                }
                if (animId == 7 || animId == 13)
                {
                    _models[0].SetAnimation(14, AnimFlags.NoLoop);
                }
                AnimationInfo animInfo = _models[0].AnimInfo;
                int frameCount = animInfo.FrameCount[0];
                int animFrame = animInfo.Frame[0];
                Vector3 cameraPos = player.CameraInfo.Position;
                Vector3 position = frameCount == 0 ? playerPos.AddY(0.625f)
                    : (playerPos.AddY(0.625f) * (frameCount - animFrame) + (cameraPos + playerFacing / 2) * animFrame) / frameCount;
                SetFacing(facing.Normalized(), position);
            }
            else if (player.IsAltForm)
            {
                if (facing.Y != 0)
                {
                    if (facing.X == 0 && facing.Z == 0)
                    {
                        facing.X = 1;
                    }
                }
                if (animId == 4 || animId == 13 && _models[0].AnimInfo.Flags[0].TestFlag(AnimFlags.Ended))
                {
                    _models[0].SetAnimation(7);
                }
                SetFacing(facing.Normalized(), playerPos.AddY(0.625f));
            }
            else
            {
                if (facing.Y == 0)
                {
                    facing.Y = -playerFacing.Y;
                }
                if (animId == 7 || animId == 14 && _models[0].AnimInfo.Flags[0].TestFlag(AnimFlags.Ended))
                {
                    _models[0].SetAnimation(4);
                }
                SetFacing(facing.Normalized());
            }
            // 2 damage on every 9th DS frame (timer 8 counted down, then the hit)
            if (_drainDamageTimer > 0)
            {
                _drainDamageTimer--;
            }
            else
            {
                player.TakeDamage(2, DamageFlags.NoDmgInvuln, direction: null, this);
                _drainDamageTimer = 9 * 2 - 1; // todo: FPS stuff
            }
            if (CallSubroutine(_subroutines, this))
            {
                if (player.AttachedEnemy == this)
                {
                    player.AttachedEnemy = null;
                }
                Func21648A4();
            }
        }

        private void State09()
        {
            if (CallSubroutine(_subroutines, this))
            {
                Func21648A4();
            }
        }

        private void State10()
        {
            // FH sfx 0x41 while waiting; MPH's METROID_FLY stands in
            _soundSource.PlaySfx(SfxId.METROID_FLY, loop: true);
            State02();
        }

        // anim ended
        private bool Behavior00()
        {
            return _models[0].AnimInfo.Flags[0].TestFlag(AnimFlags.Ended);
        }

        // the player has no Metroid on them
        private bool Behavior01()
        {
            return Target.AttachedEnemy == null;
        }

        // timer
        private bool Behavior02()
        {
            if (_field170 > 0)
            {
                _field170--;
                return false;
            }
            return true;
        }

        // latch on: touching, alive, and the player has no Metroid yet
        private bool Behavior03()
        {
            if (_health == 0 || !HitPlayers[Target.SlotIndex] || Target.AttachedEnemy != null)
            {
                return false;
            }
            Target.AttachedEnemy = this;
            return true;
        }

        // just hit
        private bool Behavior04()
        {
            return _timeSinceDamage == 1;
        }

        // the player is in sight within 6.5
        private bool Behavior05()
        {
            CollisionResult result = default;
            if (CollisionDetection.CheckBetweenPoints(Position, Target.Position, TestFlags.None, _scene, ref result))
            {
                return false;
            }
            Vector3 between = Target.Position - Position;
            return between.LengthSquared < 6.5f * 6.5f;
        }

        // back at the first idle point
        private bool Behavior06()
        {
            Vector3 between = _idlePoints[0] - Position;
            return between.LengthSquared < 1;
        }

        // a bomb went off next to the latched Metroid
        private bool Behavior07()
        {
            if (!_hitByBomb)
            {
                return false;
            }
            _hitByBomb = false;
            return true;
        }

        // the player died
        private bool Behavior08()
        {
            return Target.Health == 0;
        }

        // the player is out of sight
        private bool Behavior09()
        {
            CollisionResult result = default;
            return CollisionDetection.CheckBetweenPoints(Position, Target.Position, TestFlags.None, _scene, ref result);
        }

        // the player is more than 10 away (0x20254c8; MphRead's Enemy02 has < 100)
        private bool Behavior10()
        {
            Vector3 between = Target.Position - Position;
            return between.LengthSquared > 100;
        }

        // the player already has a Metroid
        private bool Behavior11()
        {
            return Target.AttachedEnemy != null;
        }

        // the player is within 5
        private bool Behavior12()
        {
            Vector3 between = Target.Position - Position;
            return between.LengthSquared < 25;
        }

        // the cart's table (0x20733c8) is the same as Metadata.Enemy02Subroutines
        private static readonly IReadOnlyList<EnemySubroutine<FhMetroidEntity>> _subroutines = new EnemySubroutine<FhMetroidEntity>[11]
        {
            // state 0: idle patrol
            Sub((2, e => e.Behavior04()), (1, e => e.Behavior05())),
            // state 1: chase
            Sub((7, e => e.Behavior09()), (7, e => e.Behavior10()), (10, e => e.Behavior11()), (2, e => e.Behavior12())),
            // state 2: recoil
            Sub((3, e => e.Behavior00())),
            // state 3: rise back
            Sub((4, e => e.Behavior00())),
            // state 4: lunge in
            Sub((5, e => e.Behavior00())),
            // state 5: dash
            Sub((1, e => e.Behavior02()), (8, e => e.Behavior03())),
            // state 6: knocked off
            Sub((9, e => e.Behavior00())),
            // state 7: return
            Sub((1, e => e.Behavior04()), (1, e => e.Behavior05()), (0, e => e.Behavior06())),
            // state 8: latched
            Sub((6, e => e.Behavior07()), (6, e => e.Behavior02()), (6, e => e.Behavior08())),
            // state 9: die off
            Sub((7, e => e.Behavior00())),
            // state 10: wait
            Sub((7, e => e.Behavior01()))
        };

        private static EnemySubroutine<FhMetroidEntity> Sub(params (byte Next, Func<FhMetroidEntity, bool> Function)[] behaviors)
        {
            var list = new EnemyBehavior<FhMetroidEntity>[behaviors.Length];
            for (int i = 0; i < behaviors.Length; i++)
            {
                list[i] = new EnemyBehavior<FhMetroidEntity>(behaviors[i].Next, behaviors[i].Function);
            }
            return new EnemySubroutine<FhMetroidEntity>(list);
        }
    }
}
