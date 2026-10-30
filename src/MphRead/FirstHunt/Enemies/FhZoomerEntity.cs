using System;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities.FirstHunt
{
    // recomp: First Hunt's Zoomer (cart process 0x20229d4, init 0x202328c; docs/FIRST_HUNT_PORT.md P2). A copy of
    // MphRead's Enemy01Entity with the cart's differences:
    //   - wobble 3-10 degrees per frame up to 60 (MPH 3-6 up to 40), and the crawl direction is turned by it every
    //     frame (MPH only on flat ground), with the cart's copy slip: dir.y takes the new dir.x (0x2022c14)
    //   - the home volume is the spawner's Sphere (rt+0xcc); leaving it sets the intended direction (with the same
    //     y=x slip) and the direction snaps to it once within one wobble step (no 0.2 lerp, no 1-degree nudge)
    //   - collision is tested at Position (not Position + up * r) and pushes the Zoomer out directly
    //   - up eases 0.1 per frame toward the surface (MPH 0.2), sticks with -122/4096 (MPH -245), walks only when the
    //     surface is within ~35 degrees (3355/4096, MPH 3712) and settles at 4094/4096 (MPH 4095)
    //   - health 12, radius 0.25, contact 15 with knockback, hurt animation 1
    public class FhZoomerEntity : FhEnemyEntity
    {
        private Vector3 _direction;
        private float _angleInc = 0;
        private float _curAngle = 0;
        private float _maxAngle = 0;
        private float _angleCos = 0;
        private float _angleSin = 0;
        private Vector3 _intendedDir;
        private Vector3 _field1A0; // cart +0xe4: the settled surface normal (starts at zero in the cart)
        private int _volumeCheckDelay = 0;
        private bool _seekingVolume = false;
        private CollisionVolume _homeVolume;

        public FhZoomerEntity(FhEnemySpawnEntity spawner, Scene scene)
            : base(EnemyType.Zoomer, spawner, scene)
        {
        }

        protected override int HurtAnimation => 1;

        protected override void EnemyInitialize()
        {
            Vector3 spawnerPos = SpawnData.Header.Position.ToFloatVector();
            var facing = new Vector3(Rng.GetRandomInt2(4096) / 4096f, 0, Rng.GetRandomInt2(4096) / 4096f);
            if (facing.X == 0 && facing.Z == 0)
            {
                facing = SpawnData.Header.FacingVector.ToFloatVector();
            }
            facing = facing.Normalized(); // the cart doesn't, but the transform needs it (dir below is normalized anyway)
            var up = new Vector3(0, SpawnData.Header.UpVector.ToFloatVector().Y, 0);
            if (up.Y == 0)
            {
                up = Vector3.UnitY;
            }
            up = up.Normalized();
            facing = Vector3.Cross(up, Vector3.Cross(facing, up)).Normalized();
            if (Single.IsNaN(facing.X) || facing == Vector3.Zero)
            {
                facing = Vector3.UnitZ;
            }
            SetTransform(facing, up, spawnerPos);
            Flags |= EnemyFlags.Visible;
            Flags &= ~EnemyFlags.NoHomingNc;
            Flags &= ~EnemyFlags.NoHomingCo;
            _health = _healthMax = 12;
            _boundingRadius = 0.25f;
            SetUpModel("zoomer", firstHunt: true);
            _field1A0 = Vector3.Zero;
            _angleInc = Fixed.ToFloat(Rng.GetRandomInt2(0x7000)) + 3; // [3, 10)
            _angleInc /= 2; // todo: FPS stuff
            _maxAngle = Fixed.ToFloat(Rng.GetRandomInt2(0)) + 60;
            _curAngle = 0;
            float radians = MathHelper.DegreesToRadians(_angleInc);
            _angleCos = MathF.Cos(radians);
            _angleSin = MathF.Sin(radians);
            _homeVolume = CollisionVolume.Move(SpawnData.Sphere, spawnerPos);
            _direction = Vector3.Cross(facing, up).Normalized();
        }

        protected override void EnemyProcess()
        {
            ModelInstance model = _models[0];
            if (model.AnimInfo.Flags[0].TestFlag(AnimFlags.Ended) && model.AnimInfo.Index[0] != 0)
            {
                model.SetAnimation(0);
            }
            // FH sfx 0x2A (Zoomer loop); MPH's stands in
            _soundSource.PlaySfx(SfxId.ZOOMER_IDLE_LOOP, loop: true);
            if (!_seekingVolume)
            {
                if (_volumeCheckDelay > 0)
                {
                    _volumeCheckDelay--;
                }
                else if (!_homeVolume.TestPoint(Position))
                {
                    _volumeCheckDelay = 16 * 2; // todo: FPS stuff
                    Vector3 away = Position - SpawnData.Header.Position.ToFloatVector();
                    if (away.X == 0 && away.Z == 0)
                    {
                        away = FacingVector;
                        if (away.X == 0 && away.Z == 0)
                        {
                            away = SpawnData.Header.FacingVector.ToFloatVector();
                        }
                    }
                    _intendedDir = Vector3.Cross(UpVector, away);
                    _intendedDir.Y = _intendedDir.X; // the cart's slip (0x2022b28)
                    _intendedDir = SafeNormalize(_intendedDir, _direction);
                    _seekingVolume = true;
                }
            }
            if (!_seekingVolume)
            {
                _curAngle += _angleInc;
                if (_curAngle > _maxAngle || _curAngle < -_maxAngle)
                {
                    _angleInc *= -1;
                    _angleSin *= -1;
                }
            }
            // every frame, seeking or not: turn the direction about Y by the wobble step (0x2022b98)
            float newX = _direction.X * _angleCos - _direction.Z * _angleSin;
            float newZ = _direction.X * _angleSin + _direction.Z * _angleCos;
            _direction = SafeNormalize(new Vector3(newX, newX, newZ), _direction); // dir.y = new dir.x: the cart's slip
            if (_seekingVolume && Vector3.Dot(_intendedDir, _direction) > _angleCos)
            {
                _direction = _intendedDir;
                _seekingVolume = false;
                _curAngle = 0;
            }
            Vector3 target = UpVector * 0.5f;
            var results = new CollisionResult[8];
            int colCount = CollisionDetection.CheckInRadius(Position, _boundingRadius, limit: 8,
                getSimpleNormal: false, TestFlags.None, _scene, results);
            if (colCount > 0)
            {
                Vector3 position = Position;
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
                        pen = _boundingRadius + result.Plane.W - Vector3.Dot(position, normal);
                    }
                    Vector3 push = normal * pen;
                    if (Vector3.Dot(push, _speed) <= 0)
                    {
                        if (Vector3.Dot(_field1A0, normal) < Fixed.ToFloat(4094))
                        {
                            target += normal;
                        }
                        position += push;
                    }
                }
                if (target == Vector3.Zero)
                {
                    target = _field1A0;
                }
                target = SafeNormalize(target, UpVector);
                Vector3 upVec = UpVector + (target - UpVector) * (Fixed.ToFloat(0x199) / 2); // todo: FPS stuff
                upVec = SafeNormalize(upVec, UpVector);
                Vector3 facingVec = SafeNormalize(Vector3.Cross(upVec, _direction), FacingVector);
                if (MathF.Abs(Vector3.Dot(facingVec, upVec)) > 0.999f)
                {
                    facingVec = FacingVector;
                }
                SetTransform(facingVec, upVec, position);
            }
            _speed = UpVector * (Fixed.ToFloat(-122) / 2); // todo: FPS stuff
            if (!_seekingVolume)
            {
                float dot = Vector3.Dot(target, UpVector);
                if (dot >= Fixed.ToFloat(3355))
                {
                    _speed += FacingVector * (Fixed.ToFloat(204) / 2); // todo: FPS stuff
                    if (dot >= Fixed.ToFloat(4094))
                    {
                        _field1A0 = target;
                    }
                }
            }
            // the cart adds the knockback on all three axes; MphRead's helper does X/Z
            ContactDamagePlayer(15, knockback: true);
        }

        private static Vector3 SafeNormalize(Vector3 vec, Vector3 fallback)
        {
            float length = vec.Length;
            if (length == 0 || Single.IsNaN(length))
            {
                return fallback;
            }
            return vec / length;
        }
    }
}
