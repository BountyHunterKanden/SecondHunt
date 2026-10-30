using MphRead;
using MphRead.Entities;
using MphRead.Entities.Enemies;
using MphRead.Formats;

namespace MphRecomp.Campaign
{
    // BossSkip's Gorea half (dev "boss down" in Gorea_b1 / Gorea_b2, owner queue #36: a quick way to the endings). Same
    // rules: once Gorea is in the fight and the player has had control for a second, the parts the game has opened to
    // damage take steady hits through the game's own damage path, so every phase, transform and movie is the game's.
    //   Gorea_b1 (91): the arms (no beam source: plain damage, weapon colours don't matter; an arm dies past 120), then
    //     1B's seal sphere, hit up to the phase's 1000 / 2000 / 3000 mark and no further (1B checks the total on its own
    //     tick; extra damage would shorten the next phase). 1B and 1A swap three times, then 1B's Behavior02 plays the
    //     bad ending, or Gorea 2's intro when trigger state bit 12 (the seal targets) is set.
    //   Gorea_b2 (92): the seal sphere only counts the Omega Cannon (and past 720 only once an Omega Cannon hit has set
    //     Bit9), so the hits come from an Omega Cannon beam of the player's that is never fired: the player never holds
    //     the cannon (holding it makes Gorea 2 flee). 840 -> Bit11 -> State17 -> the good ending movie.
    // The fight pauses for movies (Gorea 2's intro, then the room change) and picks up in the next room. Done = an ending
    // movie started (a host playing movies itself) or the scene has no Gorea left.
    public sealed class GoreaSkip
    {
        const int ControlFrames = 60;
        const ushort ArmHit = 20;
        const ushort SphereHit = 20;
        const ushort OmegaHit = 120; // one seal-sphere mark (the sphere closes at every 120 until Gorea teleports back)
        int _readyFrames;
        int _frames;
        int _hits;
        int _movieSerial = -1;
        int _room = -1;
        BeamProjectileEntity? _omega;

        public static bool IsGoreaRoom(int roomId) => roomId is ShipNavigation.GoreaFirstRoomId or ShipNavigation.GoreaSecondRoomId;

        public bool Done { get; private set; }

        public bool Fighting => _movieSerial >= 0 && !Done;

        public System.Action<string>? Trace { get; set; }

        public string Report => $"{_hits} hits over {_frames} frames";

        public void Step(Scene scene)
        {
            if (Done)
            {
                return;
            }
            if (_movieSerial >= 0 && scene.HostMovieSerial != _movieSerial)
            {
                // a movie: an ending is the end of the skip; Gorea 2's intro leads to the next room
                if (scene.HostMovie is Movie.BadEndingPart1 or Movie.BadEndingPart2 or Movie.GoodEnding)
                {
                    Done = true;
                    return;
                }
                _movieSerial = -1;
                _readyFrames = 0;
            }
            if (scene.RoomId != _room)
            {
                _room = scene.RoomId;
                _movieSerial = -1;
                _readyFrames = 0;
                _omega = null;
            }
            Enemy28Entity? gorea1B = null;
            bool bossAlive = false;
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity is Enemy24Entity or Enemy31Entity)
                {
                    bossAlive = true;
                }
                else if (entity is Enemy28Entity b)
                {
                    bossAlive = true;
                    gorea1B = b;
                }
            }
            if (_movieSerial < 0)
            {
                if (!bossAlive || CameraSequence.Current != null || GameState.DialogPause)
                {
                    _readyFrames = 0;
                    if (!bossAlive && _frames > 0 && GameState.TransitionState == TransitionState.None && !IsGoreaRoom(scene.RoomId))
                    {
                        Done = true;
                    }
                    return;
                }
                if (++_readyFrames < ControlFrames)
                {
                    return;
                }
                _movieSerial = scene.HostMovieSerial;
            }
            _frames++;
            foreach (EntityBase entity in scene.Entities)
            {
                switch (entity)
                {
                case Enemy26Entity arm:
                    if (Open(arm) && !arm.ArmFlags.TestFlag(GoreaArmFlags.Bit0))
                    {
                        Hit(arm, ArmHit, source: null, $"arm #{arm.Id} damage {arm.Damage}");
                    }
                    break;
                case Enemy29Entity sphere1:
                    if (gorea1B != null && Open(sphere1))
                    {
                        int floor = 1000 * (4 - gorea1B.PhasesLeft);
                        if (sphere1.Damage < floor)
                        {
                            Hit(sphere1, (ushort)System.Math.Min(SphereHit, floor - sphere1.Damage), source: null,
                                $"seal sphere 1 damage {sphere1.Damage} (phases left {gorea1B.PhasesLeft})");
                        }
                    }
                    break;
                case Enemy32Entity sphere2:
                    if (Open(sphere2))
                    {
                        _omega ??= new BeamProjectileEntity(scene)
                        {
                            Owner = PlayerEntity.Main,
                            Beam = BeamType.OmegaCannon,
                            BeamKind = BeamType.OmegaCannon
                        };
                        _omega.Position = sphere2.Position;
                        Hit(sphere2, OmegaHit, _omega, $"seal sphere 2 damage {sphere2.Damage}");
                    }
                    break;
                }
            }
        }

        // what a player's shot could hurt right now: beams collide with it and it isn't invincible (1B runs hidden from the
        // start and its sphere drops Invincible long before 1B comes out; only its Activate turns beam collision on)
        static bool Open(EnemyInstanceEntity part) => part.Flags.TestFlag(EnemyFlags.CollideBeam) && !part.Flags.TestFlag(EnemyFlags.Invincible);

        void Hit(EnemyInstanceEntity part, ushort damage, EntityBase? source, string what)
        {
            part.TakeDamage(damage, source);
            _hits++;
            Trace?.Invoke($"hit {what}");
        }
    }
}
