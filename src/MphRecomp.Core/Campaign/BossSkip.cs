using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using MphRead.Entities.Enemies;
using MphRead.Formats;

namespace MphRecomp.Campaign
{
    // Dev start "boss defeated" (owner 2026-10-03: test the escape sounds without fighting the boss first). Once the boss
    // is in the fight and the player has had control for a second (its intro camera sequence done), every boss part the
    // game has opened to damage takes a hit of HitDamage through the game's own damage path, each frame: the Cretaphid's
    // eyes and crystal whenever they're not invincible, the Slench's synapses likewise, and the Slench through its shield
    // only in the window the shield passes hits on (eye open, vulnerable). Steady hits, not one-shots: the bosses change
    // phase at health thresholds, and a one-shot kill skips them. The Cretaphid's crystal is only compared with its phase's
    // value now and then, so it is hit down to that value and no further (a crystal taken to 0 in phase 0 trips the
    // Cretaphid's state machine: MphRead asserts, the game would read a freed crystal). So the fight runs
    // its own phases at a fast player's pace, and everything after is the game's: the death movie, the music, the boss
    // flags and the spawner's messages that bring the Octolith out. Call Step after every sim frame until Done (the death movie started
    // or the boss is gone).
    public sealed class BossSkip
    {
        const int ControlFrames = 60;
        const ushort HitDamage = 20;
        int _readyFrames;
        int _frames;
        int _hits;
        int _movieSerial = -1;
        readonly List<EnemyInstanceEntity> _parts = new();

        public bool Done { get; private set; }

        // the intro is over and the hits are landing (the host may run the sim fast meanwhile)
        public bool Fighting => _movieSerial >= 0 && !Done;

        // every hit, for the log (part, health before -> after)
        public System.Action<string>? Trace { get; set; }

        // how the fight went, for the log
        public string Report => $"{_hits} hits over {_frames} frames";

        public void Step(Scene scene)
        {
            if (Done)
            {
                return;
            }
            Enemy41Entity? slench = null;
            Enemy19Entity? cretaphid = null;
            bool bossAlive = false;
            _parts.Clear();
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity is not EnemyInstanceEntity enemy || enemy.Health == 0)
                {
                    continue;
                }
                switch (enemy.EnemyType)
                {
                case EnemyType.Cretaphid:
                    bossAlive = true;
                    cretaphid = (Enemy19Entity)enemy;
                    break;
                case EnemyType.Slench:
                    bossAlive = true;
                    slench = (Enemy41Entity)enemy;
                    break;
                case EnemyType.CretaphidEye:
                case EnemyType.CretaphidCrystal:
                case EnemyType.SlenchShield:
                case EnemyType.SlenchSynapse:
                    _parts.Add(enemy);
                    break;
                }
            }
            if (_movieSerial < 0)
            {
                if (!bossAlive || CameraSequence.Current != null || GameState.DialogPause)
                {
                    _readyFrames = 0;
                    return;
                }
                if (++_readyFrames < ControlFrames)
                {
                    return;
                }
                _movieSerial = scene.HostMovieSerial;
            }
            if (!bossAlive || scene.HostMovieSerial != _movieSerial)
            {
                Done = true;
                return;
            }
            _frames++;
            foreach (EnemyInstanceEntity part in _parts)
            {
                if (part.EnemyType == EnemyType.SlenchShield)
                {
                    if (slench == null || slench.SlenchFlags.TestFlag(SlenchFlags.EyeClosed)
                        || !slench.SlenchFlags.TestFlag(SlenchFlags.Vulnerable))
                    {
                        continue;
                    }
                }
                else if (part.Flags.TestFlag(EnemyFlags.Invincible))
                {
                    continue;
                }
                ushort damage = HitDamage;
                if (part.EnemyType == EnemyType.CretaphidCrystal && cretaphid != null)
                {
                    int floor = CrystalFloor(cretaphid);
                    if (part.Health <= floor)
                    {
                        continue;
                    }
                    damage = (ushort)System.Math.Min(damage, part.Health - floor);
                }
                ushort before = part.Health;
                part.TakeDamage(damage, source: null);
                _hits++;
                Trace?.Invoke($"hit {part.EnemyType} #{part.Id}: {before} -> {part.Health}");
            }
        }

        // the crystal health at which the Cretaphid leaves its current phase (its Behavior09 compares with this)
        static int CrystalFloor(Enemy19Entity cretaphid)
        {
            Enemy19Values values = cretaphid.Values;
            return cretaphid.PhaseIndex switch
            {
                0 => values.Phase0CrystalHealth,
                1 => values.Phase1CrystalHealth,
                _ => values.Phase2CrystalHealth
            };
        }
    }
}
