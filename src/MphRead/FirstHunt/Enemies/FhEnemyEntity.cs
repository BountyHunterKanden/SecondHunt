using System;
using System.Collections.Generic;
using MphRead.Formats.Culling;
using OpenTK.Mathematics;

namespace MphRead.Entities.FirstHunt
{
    // recomp: the base of First Hunt's enemies (docs/FIRST_HUNT_PORT.md P2; cart enemy instance 0x202cbd4, damage
    // 0x202cf64). They are the ancestors of MPH's enemies 00-06 and keep their EnemyType (same order), but they are not
    // MPH's: one bounding sphere for contact and hits (no hurt volume from the spawner), no weaknesses, a hurt sound and
    // animation, a 6-frame swell when hit, and the spawner counts them itself. The per-type behaviour is in the
    // subclasses (copies of MphRead's MPH enemy with First Hunt's numbers). Timers in the cart are 30 Hz frames.
    public abstract class FhEnemyEntity : EnemyInstanceEntity
    {
        protected readonly FhEnemySpawnEntity _spawner;
        protected FhEnemySpawnEntityData SpawnData => _spawner.Data;
        private int _sinceHit = 255; // DS frames since the last hit (the swell)

        // the cart's hurt and death sounds by type (tables 0x2074cf4, 0x2074d94; SFXDATA indices)
        private static readonly int[] _hurtSfx = { 49, 41, 63, 43, 43, 43, 43 };
        private static readonly int[] _deathSfx = { 50, 40, 64, 44, 44, 44, 44 };

        // a kill (mission scoring, FirstHuntMission): the enemy and the player whose shot or bomb killed it
        public static Action<FhEnemyEntity, PlayerEntity?>? Killed { get; set; }

        protected FhEnemyEntity(EnemyType type, FhEnemySpawnEntity spawner, Scene scene)
            : base(new EnemyInstanceEntityData(type, spawner), spawner.NodeRef, scene)
        {
            _spawner = spawner;
        }

        public static FhEnemyEntity? Create(FhEnemySpawnEntity spawner, Scene scene)
        {
            return spawner.Data.EnemyType switch
            {
                FhEnemyType.WarWasp => new FhWarWaspEntity(spawner, scene),
                FhEnemyType.Zoomer => new FhZoomerEntity(spawner, scene),
                FhEnemyType.Metroid => new FhMetroidEntity(spawner, scene),
                FhEnemyType.Mochtroid1 => new FhMochtroid1Entity(spawner, scene),
                FhEnemyType.Mochtroid2 => new FhMochtroid2Entity(spawner, scene),
                FhEnemyType.Mochtroid3 => new FhMochtroid3Entity(spawner, scene),
                FhEnemyType.Mochtroid4 => new FhMochtroid4Entity(spawner, scene),
                _ => null
            };
        }

        // the subclass's EnemyInitialize sets _boundingRadius (the contact/hit sphere) and the model
        public override void Initialize()
        {
            base.Initialize();
            Flags &= ~EnemyFlags.CollidePlayer;
            _hurtVolumeInit = new CollisionVolume(Vector3.Zero, _boundingRadius);
            _hurtVolume = CollisionVolume.Move(_hurtVolumeInit, Position);
        }

        public override bool Process()
        {
            // left behind: no player within 25 across and 15 up/down (0x202cc04)
            bool near = false;
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                Vector3 between = player.Position - Position;
                if (between.Xz.LengthSquared < 25 * 25 && MathF.Abs(between.Y) < 15)
                {
                    near = true;
                    break;
                }
            }
            if (!near)
            {
                _spawner.EnemyRemoved();
                return false;
            }
            if (_timeSinceDamage < 510)
            {
                _timeSinceDamage++;
            }
            if (_sinceHit < 255 * 2)
            {
                _sinceHit++;
            }
            if (_health == 0)
            {
                _spawner.EnemyDied();
                return false;
            }
            _state1 = _state2;
            _subId = _state1;
            _prevPos = Position;
            Position += _speed;
            _hurtVolume = CollisionVolume.Move(_hurtVolumeInit, Position);
            ClearHitPlayers();
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                if (player.Health > 0)
                {
                    float reach = _boundingRadius + Fixed.ToFloat(player.Values.BipedColRadius);
                    HitPlayers[player.SlotIndex] = Vector3.DistanceSquared(player.Position, Position) < reach * reach;
                }
            }
            _soundSource.Update(Position, Metadata.EnemyAudioRangeIndices[(int)EnemyType]);
            EnemyProcess();
            _hurtVolume = CollisionVolume.Move(_hurtVolumeInit, Position);
            if (NodeRef != NodeRef.None)
            {
                NodeRef = _scene.UpdateNodeRef(NodeRef, _prevPos, Position);
            }
            // the swell: 1 + 0.15 per frame for 3 frames, back down by 6 (0x202cd88)
            int n = _sinceHit / 2; // todo: FPS stuff
            _drawScale = n < 3 ? 1 + 0.15f * n : n < 6 ? 1 + 0.15f * (6 - n) : 1;
            return BaseProcess();
        }

        // the hurt animation (-1: none) and its flags
        protected virtual int HurtAnimation => -1;

        // the Metroid lets go of its player
        protected virtual void OnDeath()
        {
        }

        protected override bool FirstHuntTakeDamage(uint damage, EntityBase? source)
        {
            if (Flags.TestFlag(EnemyFlags.Invincible) || _health == 0)
            {
                return true;
            }
            if (damage < _health)
            {
                _health -= (ushort)damage;
                _timeSinceDamage = 0;
                _sinceHit = 0;
                PlaySfx(CartSfx(_hurtSfx, Metadata.EnemyDamageSfx));
                if (HurtAnimation >= 0)
                {
                    _models[0].SetAnimation(HurtAnimation, AnimFlags.NoLoop);
                }
                return true;
            }
            _health = 0;
            OnDeath();
            _soundSource.StopAllSfx();
            PlaySfx(CartSfx(_deathSfx, Metadata.EnemyDeathSfx));
            // First Hunt's own death effect (effect entity, param 0x17: effWaspDeath)
            _scene.AddEntity(FhEffectEntity.Create(_scene, Position));
            PlayerEntity? killer = source switch
            {
                BeamProjectileEntity beam => beam.Owner as PlayerEntity,
                BombEntity bomb => bomb.Owner,
                PlayerEntity player => player, // (a host or test crediting a player directly)
                _ => null
            };
            Killed?.Invoke(this, killer);
            return true;
        }

        private int CartSfx(int[] cart, IReadOnlyList<int> standIn)
        {
            int type = (int)EnemyType;
            int id = type < cart.Length ? FirstHuntSounds.Sfx(cart[type]) : -1;
            return id >= 0 ? id : standIn[type];
        }

        protected void PlaySfx(int sfx, bool loop = false)
        {
            if (sfx != -1)
            {
                _soundSource.PlaySfx(sfx & ~0xA0000, loop: loop);
            }
        }

        protected PlayerEntity Target => PlayerEntity.Main; // (the cart's enemies watch player 1)
    }
}
