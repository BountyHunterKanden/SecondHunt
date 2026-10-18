using MphRead.Entities.FirstHunt;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: First Hunt's enemy spawner (docs/FIRST_HUNT_PORT.md P2; cart init 0x2023820, process 0x202355c), on
    // MphRead's display stub while a First Hunt level runs (HostFirstHunt). Read from the cart:
    //   - dormant until the level's frame reaches StartFrame (NodeName would also need player 1 in that node: unused)
    //   - spawns at its own position, SpawnCount per burst, while player 1 is within 15 and fewer than SpawnLimit are
    //     alive and Cooldown frames have passed; SpawnTotal 0 = no end
    //   - a death restarts the cooldown; an enemy left 25 away is removed and refunded (it can come back at once)
    //   - when SpawnTotal are spawned and all dead: EmptyMessage (always Trigger) once to ParentId, param -1
    // It ignores every message.
    public partial class FhEnemySpawnEntity
    {
        private bool _fhLive = true;
        private bool _fhDormant = true;
        private int _fhAge = 0;
        private int _fhSpawned = 0;
        private int _fhAlive = 0;
        private int _fhTimer = 0;
        private EntityBase? _fhParent = null;

        public FhEnemySpawnEntityData Data => _data;
        public int SpawnedCount => _fhSpawned;
        public int AliveCount => _fhAlive;
        public bool Finished => !_fhLive; // (0x20234fc counts the spawners not finished)

        public override void Initialize()
        {
            base.Initialize();
            if (_data.ParentId != -1)
            {
                _scene.TryGetEntity(_data.ParentId, out _fhParent);
            }
            // the death effect's model (FhEffectEntity), loaded ahead like SceneSetup's enemy resources
            FhEffectEntity.Preload();
        }

        public override bool Process()
        {
            if (!PlayerEntity.HostFirstHunt || !_fhLive)
            {
                return base.Process();
            }
            int cooldown = _data.Cooldown * 2; // todo: FPS stuff
            if (_fhTimer < cooldown)
            {
                _fhTimer++;
            }
            if (_fhDormant && _fhAge++ >= _data.StartFrame * 2)
            {
                _fhDormant = false;
            }
            if (_fhDormant)
            {
                return base.Process();
            }
            int total = _data.SpawnTotal;
            if (Vector3.DistanceSquared(PlayerEntity.Main.Position, Position) < 15 * 15
                && _fhAlive < _data.SpawnLimit && _fhTimer >= cooldown && (total == 0 || _fhSpawned < total))
            {
                for (int i = 0; i < _data.SpawnCount && _fhAlive < _data.SpawnLimit && (total == 0 || _fhSpawned < total); i++)
                {
                    FhEnemyEntity? enemy = FhEnemyEntity.Create(this, _scene);
                    if (enemy == null)
                    {
                        break;
                    }
                    _scene.AddEntity(enemy);
                    _fhAlive++;
                    _fhSpawned++;
                    _fhTimer = 0;
                }
            }
            if (total != 0 && _fhSpawned >= total && _fhAlive == 0)
            {
                _fhLive = false;
                FirstHuntMessages.Send(_scene, _data.EmptyMessage, this, _fhParent, -1);
            }
            return base.Process();
        }

        // the enemy died (0x202ccf0): the cooldown starts again
        public void EnemyDied()
        {
            _fhAlive--;
            _fhTimer = 0;
        }

        // the enemy was left behind (0x202cc04): refunded, and the spawner may spawn again right away
        public void EnemyRemoved()
        {
            _fhAlive--;
            _fhSpawned--;
            _fhTimer = _data.Cooldown * 2;
        }
    }
}
