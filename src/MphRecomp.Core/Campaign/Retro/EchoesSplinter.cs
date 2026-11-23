using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Culling;
using MphRecomp.Arenas;
using MphRecomp.Import.Enemies;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign.Retro
{
    // Echoes' Splinter (SPTR, the pilot of docs/ENEMY_IMPORT.md E1) as an MPH enemy: its own AI state machine (state.fsm2,
    // FSM2 6f7f09cd, run by Fsm2Runner) with its triggers and states doing what the game's Splinter code does (the RSO
    // map in ENEMY_IMPORT.md E1, addresses there), every number read from the imported enemy.cfg (the disc's reference
    // placement), its clips from the imported body. A lone Splinter's path through the machine: Patrol (idle, fidgets)
    // -> sees you -> Alert -> PathFind (runs at you) -> in range: turn to face you -> Jump at you (the pounce is its
    // attack) -> lands, slides -> taunts or steps aside -> PathFind again. The draw is the app's (CampaignRetroBody);
    // this is the sim, run in MPH units.
    //
    // Ours, not the game's (noted in ENEMY_IMPORT.md E1): the path is a straight line at you (MPH rooms carry no Echoes
    // nav mesh); gravity (Gravity); the hit reaction pause; the beam -> Echoes weapon map (Family); health x HealthScale.
    public sealed class EchoesSplinter : EnemyInstanceEntity
    {
        // Echoes metres -> MPH units (Samus is ~2.7 m in Prime, 1.6 in MPH)
        public const float Unit = 0.593f;
        // Echoes health (Power Beam 1 a shot) -> MPH's (Power Beam 6 a shot, charged 36): x3, three uncharged shots
        public const float HealthScale = 3f;
        // ours: a jump's fall (Echoes' own is the RSO's ballistic arc with the character's gravity; not read yet)
        public const float Gravity = 35f * Unit;
        public const float Step = 1 / 60f;

        // the enemy.cfg property paths (Patterned b3774750 and the Splinter's own; docs/ENEMY_IMPORT.md E1)
        const string P = "b3774750/";
        const string PSpeed = P + "6392404e", PTurn = P + "020c78bb", PDetect = P + "8db77ee4", PDetectAngle = P + "83dfc40f",
            PMinAttack = P + "58434916", PMaxAttack = P + "ff77c96f", PContact = P + "d756416e/f2d02613",
            PDamageWait = P + "e0cdc7e3", PHealth = P + "cf90d15e/f0668919", PRadius = P + "8a6ab139", PHeight = P + "3011b5df",
            PVuln = P + "7b71ae90/", PVulnValue = "/852d3871",
            PFidgetMin = "b8ed9ffa", PFidgetMax = "5e8d301b", PTauntMin = "726cd31d", PTauntMax = "376e909f",
            PAttack = "66dcaacb/f2d02613";
        // the vulnerability struct's weapons
        public const string VPower = "ac8bb2a7", VDark = "b19b6d6c", VLight = "002444c6", VAnnihilator = "e6bacde5",
            VBomb = "68ae13a0", VMissile = "00efe8cb";

        // the clips (on the disc: two of the taunts are spelt "spllinter")
        public const string Ready = "B_ready_splinter", Run = "B_run_splinter", Alert = "B_alert_1_splinter",
            Sniff = "B_sniff_splinter", TurnLeft = "B_90turnleft_splinter", TurnRight = "B_90turnright_splinter",
            JumpInto = "B_jump_into_splinter", JumpArc = "B_jump_arc_splinter", Land = "B_landSlideForward_splinter",
            StepFwd = "StepForward", StepBack = "B_stepback_splinter", StepLeft = "B_sstepl_splinter", StepRight = "B_sstepr_splinter",
            Taunt1 = "B_taunt_1_spllinter", Taunt2 = "B_taunt_2_spllinter", Taunt3 = "B_taunt_3_splinter",
            Death = "R_death_1_splinter", HitFront = "R_frontLightHit_splinter", HitBack = "R_backLightHit_splinter",
            HitLeft = "R_leftLightHit_splinter", HitRight = "R_rightLightHit_splinter";

        public readonly RetroEnemyConfig Config;
        public readonly Fsm2Runner Brain;
        public string BodyDir => Path.Combine(Config.Dir, EnemyImportKind.BodyDir);

        // the draw's view of it: the clip (a new Serial restarts it), its time, loop; the facing (radians about Y, 0 = +Z)
        public string Clip { get; private set; } = Ready;
        public int ClipSerial { get; private set; }
        public bool ClipLoop { get; private set; } = true;
        public float ClipTime { get; private set; }
        public float Yaw { get; private set; }
        public bool Dying { get; private set; }
        public float Hp { get; private set; }
        public float HpMax { get; private set; }
        public bool Grounded { get; private set; } = true;
        public int Jumps { get; private set; }
        public int Hits { get; private set; }
        public float DamageDealt { get; private set; }
        // the red damage tint frames, as MPH enemies (EnemyInstanceEntity.GetDrawInfo)
        public bool HitFlash => _timeSinceDamage < 5 * 2;
        public readonly List<string> Visited = new();

        // its numbers (MPH units, seconds, radians)
        readonly float _speedMul, _turnRate, _detect, _detectCos, _minAttack, _maxAttack, _contact, _attackDamage, _damageWait,
            _radius, _height, _fidgetMin, _fidgetMax;
        readonly int _tauntMin, _tauntMax;
        readonly Random _rng;

        // trigger state
        bool _canSee, _attacked, _landed;
        float _seeTimer, _turnTime, _stuckTime, _hurtTime, _contactCool, _fidget, _alertDelay, _deathTime;
        int _tauntCount, _attackFails, _lastTaunt = -1, _lastStep = -1;
        Vector3 _vel;   // units / s (the jump's and the slide's; the walk's is set each tick)
        Vector3 _stepDir;
        float _stepSpeed;
        string _moveClip = "";

        static readonly Dictionary<string, Dictionary<string, float>> _durations = new(StringComparer.OrdinalIgnoreCase);

        public EchoesSplinter(int id, RetroEnemyConfig config, Vector3 position, float yaw, Scene scene, int seed = 0, EntityBase? owner = null)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, owner!), NodeRef.None, scene)
        {
            if (config.Fsm == null) throw new InvalidDataException($"{config.Dir}: no {EnemyImportKind.FsmFile}");
            Id = id;
            Config = config;
            Position = position;
            Yaw = yaw;
            _rng = new Random(seed != 0 ? seed : id * 7919 + 17);
            _speedMul = config.F32(PSpeed, 1);
            _turnRate = MathHelper.DegreesToRadians(config.F32(PTurn, 120));
            _detect = config.F32(PDetect, 80) * Unit;
            _detectCos = MathF.Cos(MathHelper.DegreesToRadians(config.F32(PDetectAngle, 180)) / 2);
            _minAttack = config.F32(PMinAttack, 3) * Unit;
            _maxAttack = config.F32(PMaxAttack, 14) * Unit;
            _contact = config.F32(PContact, 5);
            _attackDamage = config.F32(PAttack, 5);
            _damageWait = config.F32(PDamageWait, 2);
            _radius = config.F32(PRadius, 0.5f) * Unit;
            _height = config.F32(PHeight, 1) * Unit;
            _fidgetMin = config.F32(PFidgetMin, 5);
            _fidgetMax = config.F32(PFidgetMax, 18);
            _tauntMin = config.I32(PTauntMin, 0);
            _tauntMax = config.I32(PTauntMax, 1);
            HpMax = Hp = config.F32(PHealth, 6) * HealthScale;
            Brain = new Fsm2Runner(config.Fsm) { Condition = Holds, Entered = Enter };
        }

        // a weapon's vulnerability (100 = normal), from the disc
        public float Vulnerability(string weapon) => VulnerabilityOf(Config, weapon);

        public static float VulnerabilityOf(RetroEnemyConfig c, string weapon) => c.F32(PVuln + weapon + PVulnValue, 100);

        // which Echoes weapon an MPH hit counts as (ours): missiles as Missile, bombs as Bomb, every beam as Power
        public static string Family(EntityBase? source) => source switch
        {
            BeamProjectileEntity b when b.Beam == BeamType.Missile => VMissile,
            BombEntity => VBomb,
            _ => VPower,
        };

        public static Vector3 Facing(float yaw) => new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));

        // model -> world for its imported body: the bind feet on its position, the disc's metres in MPH units (not the
        // player-height fit NativeBody.ModelMatrix gives a hunter), model +Z turned to its facing
        public static Matrix4 ModelMatrix(NativeBody body, Vector3 position, float yaw)
        {
            Vector3 lateral = Facing(yaw);
            var turn = Matrix4.Identity;
            turn.Row0.Xyz = Vector3.Cross(Vector3.UnitY, lateral);
            turn.Row1.Xyz = Vector3.UnitY;
            turn.Row2.Xyz = lateral;
            return Matrix4.CreateTranslation(0, -body.Feet, 0) * Matrix4.CreateScale(Unit) * turn * Matrix4.CreateTranslation(position);
        }

        // Hard Mode's health buff (EnemyPlacement.Tick, once as it appears)
        public void ScaleHealth(float mul)
        {
            HpMax *= mul;
            Hp *= mul;
            _health = _healthMax = (ushort)Math.Clamp(MathF.Ceiling(Hp), 1, UInt16.MaxValue);
        }

        // the body's clip map (the app draws it with NativeBodyAnimator; the clips are chosen here, by Clip)
#pragma warning disable CA2255 // registers its map with BodyClips before any body loads; no other caller needed
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void InitClips() => BodyClips.Register(b => b.Clip(Run) != null ? new BodyClips { Idle = Ready } : null);
#pragma warning restore CA2255

        protected override void EnemyInitialize()
        {
            Flags = EnemyFlags.Visible | EnemyFlags.CollidePlayer | EnemyFlags.CollideBeam | EnemyFlags.NoMaxDistance
                | EnemyFlags.OnRadar;
            _boundingRadius = _radius;
            _health = _healthMax = (ushort)Math.Clamp(MathF.Ceiling(Hp), 1, UInt16.MaxValue);
            _hurtVolumeInit = new CollisionVolume(new Vector3(0, _height / 2, 0), Math.Max(_radius, _height / 2));
            Face();
            _hurtVolume = CollisionVolume.Transform(_hurtVolumeInit, Transform);
            NodeRef = PickupPlacement.NodeAt(_scene, Position);
            Brain.Start();
        }

        public override bool GetTargetable() => !Dying && base.GetTargetable();

        void Face() => SetTransform(Facing(Yaw), Vector3.UnitY, Position);

        // ---- clips ----

        float Duration(string clip)
        {
            Dictionary<string, float> d;
            lock (_durations)
            {
                if (!_durations.TryGetValue(BodyDir, out d!))
                {
                    d = new Dictionary<string, float>(StringComparer.Ordinal);
                    if (NativeBody.Exists(BodyDir))
                    {
                        foreach ((string k, HdGun.Anim a) in NativeBody.Load(BodyDir).Data.Anims) d[k] = a.Duration;
                    }
                    _durations[BodyDir] = d;
                }
            }
            return d.TryGetValue(clip, out float t) ? t : 1;
        }

        void Play(string clip, bool loop)
        {
            Clip = clip;
            ClipLoop = loop;
            ClipTime = 0;
            ClipSerial++;
        }

        bool ClipOver => !ClipLoop && ClipTime >= Duration(Clip);

        // ---- the state machine's triggers (the RSO's; the ones a lone Splinter never meets are false) ----

        Vector3 PlayerPos => PlayerEntity.Main.Position;

        // where it heads: the player, no further than 25 m (the RSO clamps its target point)
        Vector3 Target
        {
            get
            {
                Vector3 d = PlayerPos - Position;
                float max = 25 * Unit;
                return d.Length > max ? Position + d.Normalized() * max : PlayerPos;
            }
        }

        float AngleTo(Vector3 to)
        {
            Vector3 d = to - Position;
            if (d.Xz.LengthSquared < 1e-8f) return 0;
            float want = MathF.Atan2(d.X, d.Z);
            return Wrap(want - Yaw);
        }

        static float Wrap(float a)
        {
            while (a > MathF.PI) a -= MathF.Tau;
            while (a < -MathF.PI) a += MathF.Tau;
            return a;
        }

        bool Holds(string name, float arg)
        {
            switch (name)
            {
            case "CanSeePlayer": return _canSee;
            case "Attacked": return _attacked;
            case "AnimOver": return ClipOver;
            case "Stuck": return _stuckTime > 1;
            case "IsMelee": return true;
            case "InAttackRange":
            {
                float d2 = (PlayerPos - Position).LengthSquared;
                return d2 > 0 && d2 < _maxAttack * _maxAttack;
            }
            case "FacingPlayer": return MathF.Abs(AngleTo(PlayerPos)) < 0.15708f;
            case "TooMuchTurning":
                if (_turnTime <= 4) return false;
                _tauntCount = 0;
                return true;
            case "ShouldTaunt": return _tauntCount > 0;
            case "AttackPointIsLegal":
            {
                CollisionResult res = default;
                Vector3 from = Position.AddY(_height);
                if (!CollisionDetection.CheckBetweenPoints(from, Target.AddY(_height), TestFlags.None, _scene, ref res)) return true;
                return ++_attackFails > 5;
            }
            case "Landed": return _landed;
            case "Delay": return Brain.TimeInState >= arg;
            }
            return false;
        }

        void Enter(int from, int to)
        {
            string name = Brain.Machine.States[to].Name;
            if (Visited.Count == 0 || Visited[^1] != name) Visited.Add(name);
            _attacked = false;
            _moveClip = "";
            switch (name)
            {
            case "Patrol":
                Play(Ready, loop: true);
                _fidget = Lerp(_fidgetMin, _fidgetMax, (float)_rng.NextDouble());
                break;
            case "Alert":
                _alertDelay = (float)_rng.NextDouble();
                Play(Ready, loop: true);
                break;
            case "PathFind":
                Play(Run, loop: true);
                _stuckTime = 0;
                _turnTime = 0;
                break;
            case "TurnToPlayer":
                Play(Ready, loop: true);
                break;
            case "Taunt":
                if (_tauntCount > 0) _tauntCount--;
                Variant(new[] { 5, 0, 2, 3, 4, 1, 6 }, ref _lastTaunt);
                break;
            case "Sidestep":
                Variant(new[] { 0, 2, 5, 6, 1, 3, 4 }, ref _lastStep);
                break;
            case "Jump":
                _tauntCount = _rng.Next(_tauntMin, _tauntMax + 1);
                _attackFails = 0;
                Launch();
                break;
            case "Dead":
                Play(Death, loop: false);
                break;
            }
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // a taunt's or a sidestep's variant (never the last one again): 0/2/5/6 step (left, right, back, forward),
        // 1/3/4 a taunt clip
        void Variant(int[] list, ref int last)
        {
            int v;
            do v = list[_rng.Next(list.Length)]; while (v == last && list.Length > 1);
            last = v;
            _stepDir = Vector3.Zero;
            _stepSpeed = 0;
            Vector3 f = Facing(Yaw), r = new Vector3(-f.Z, 0, f.X);
            switch (v)
            {
            case 0: StepTo(StepLeft, -r); break;
            case 2: StepTo(StepRight, r); break;
            case 5: StepTo(StepBack, -f); break;
            case 6: StepTo(StepFwd, f); break;
            case 1: Play(Taunt2, loop: false); break;
            case 3: Play(Taunt1, loop: false); break;
            default: Play(Taunt3, loop: false); break;
            }
        }

        void StepTo(string clip, Vector3 dir)
        {
            Play(clip, loop: false);
            _stepDir = dir;
            _stepSpeed = ClipSpeed(clip);
        }

        float ClipSpeed(string clip) => clip switch
        {
            // body.cfg's travel speeds (m/s), as MPH units
            Run => 5.6293f, StepFwd => 4.2795f, StepBack => 3.4245f, StepLeft => 3.183f, StepRight => 3.18f, JumpArc => 12.6812f,
            Land => 11.5983f, _ => 0,
        } * Unit * _speedMul;

        // the pounce: a ballistic arc at the target at the jump clip's speed
        void Launch()
        {
            Vector3 to = Target;
            Vector3 d = to - Position;
            float horiz = d.Xz.Length;
            float speed = ClipSpeed(JumpArc);
            float t = Math.Max(0.35f, horiz / Math.Max(speed, 0.01f));
            Vector2 h = horiz > 1e-4f ? d.Xz / horiz * (horiz / t) : Vector2.Zero;
            float vy = (d.Y + 0.5f * Gravity * t * t) / t;
            _vel = new Vector3(h.X, vy, h.Y);
            Grounded = false;
            _landed = false;
            Jumps++;
            Play(JumpInto, loop: false);
            _moveClip = JumpArc;
        }

        // ---- damage ----

        protected override bool FirstHuntTakeDamage(uint damage, EntityBase? source)
        {
            if (Dying) return true;
            if (source is BeamProjectileEntity own && own.Owner?.Type == EntityType.EnemyInstance) return true;
            float vuln = Vulnerability(Family(source)) / 100f;
            Effectiveness eff = vuln <= 0 ? Effectiveness.Zero : vuln < 1 ? Effectiveness.Half : vuln > 1 ? Effectiveness.Double : Effectiveness.Normal;
            (source as BeamProjectileEntity)?.SpawnDamageEffect(eff);
            if (vuln <= 0) return true;
            Hp -= damage * vuln;
            Hits++;
            _attacked = true;
            _timeSinceDamage = 0;
            if (Hp <= 0)
            {
                Hp = 0;
                Die(source);
                return true;
            }
            _health = (ushort)Math.Clamp(MathF.Ceiling(Hp), 1, UInt16.MaxValue);
            // the light-hit reaction from the side it was hit on (not mid-jump)
            if (Grounded && Brain.StateName != "Jump")
            {
                Vector3 from = source != null ? HitFrom(source) : Position + Facing(Yaw);
                float a = AngleTo(from);
                string clip = MathF.Abs(a) < MathF.PI / 4 ? HitFront : MathF.Abs(a) > 3 * MathF.PI / 4 ? HitBack : a > 0 ? HitLeft : HitRight;
                Play(clip, loop: false);
                _hurtTime = Math.Min(Duration(clip), 0.6f);
            }
            return true;
        }

        static Vector3 HitFrom(EntityBase source)
        {
            source.GetPosition(out Vector3 p);
            if (source is BeamProjectileEntity b && b.Owner != null) b.Owner.GetPosition(out p);
            return p;
        }

        void Die(EntityBase? source)
        {
            Dying = true;
            Flags |= EnemyFlags.Invincible;
            _health = 1;   // MphRead removes it at 0: kept until the death clip ends
            _vel = Vector3.Zero;
            _speed = Vector3.Zero;
            int dead = Brain.Find("Dead");
            if (dead >= 0) Brain.Force(dead);
            else Play(Death, loop: false);
            if (GameState.StorySave.Stats.EnemyKills != UInt32.MaxValue) GameState.StorySave.Stats.EnemyKills++;
            if (GameEvents.Any) GameEvents.Raise("enemy_killed", "mp2:Splinter", Id, ((source as BeamProjectileEntity)?.Owner as PlayerEntity)?.SlotIndex ?? -1);
        }

        // ---- per tick ----

        protected override void EnemyProcess()
        {
            ClipTime += Step;
            if (_contactCool > 0) _contactCool -= Step;
            if (Dying)
            {
                _speed = Vector3.Zero;
                _deathTime += Step;
                if (_deathTime >= Duration(Death) + 0.3f) _health = 0;
                return;
            }
            Sense();
            Vector3 before = Position;
            if (_hurtTime > 0)
            {
                _hurtTime -= Step;
                if (_hurtTime <= 0) Resume();
                _speed = Vector3.Zero;
            }
            else
            {
                Brain.Tick(Step);
                Act();
            }
            Ground();
            Contact();
            Face();
            if (Brain.StateName == "PathFind")
            {
                float want = ClipSpeed(Run) * Step;
                float moved = (Position + _speed - before).Xz.Length;
                _stuckTime = moved < want * 0.2f ? _stuckTime + Step : 0;
            }
        }

        // back to the state's own clip after a hit reaction
        void Resume()
        {
            switch (Brain.StateName)
            {
            case "PathFind": Play(Run, loop: true); break;
            case "Taunt" or "Sidestep" or "Alert" or "Jump" or "Dead": break;
            default: Play(Ready, loop: true); break;
            }
        }

        // the sight check, every 0.4 s: within range and the view cone, and nothing in between (the ray is ours)
        void Sense()
        {
            _seeTimer -= Step;
            if (_seeTimer > 0) return;
            _seeTimer = 0.4f;
            PlayerEntity p = PlayerEntity.Main;
            _canSee = false;
            if (p.Health <= 0) return;
            Vector3 d = p.Position - Position;
            if (d.LengthSquared > _detect * _detect) return;
            Vector3 flat = new Vector3(d.X, 0, d.Z);
            if (flat.LengthSquared > 1e-6f && Vector3.Dot(flat.Normalized(), Facing(Yaw)) < _detectCos - 1e-4f) return;
            CollisionResult res = default;
            _canSee = !CollisionDetection.CheckBetweenPoints(Position.AddY(_height), p.Position.AddY(0.8f), TestFlags.None, _scene, ref res);
        }

        void TurnToward(Vector3 to, float rate)
        {
            float a = AngleTo(to);
            float max = rate * Step;
            Yaw = Wrap(Yaw + Math.Clamp(a, -max, max));
        }

        void Act()
        {
            Vector3 walk = Vector3.Zero;
            switch (Brain.StateName)
            {
            case "Patrol":
                _fidget -= Step * ((PlayerPos - Position).LengthSquared < _detect * _detect ? 2 : 1);
                if (Clip != Ready && ClipOver) Play(Ready, loop: true);
                if (_fidget <= 0)
                {
                    Play(Sniff, loop: false);
                    _fidget = Lerp(_fidgetMin, _fidgetMax, (float)_rng.NextDouble());
                }
                break;
            case "Alert":
                TurnToward(PlayerPos, _turnRate);
                if (_alertDelay > 0)
                {
                    _alertDelay -= Step;
                    if (_alertDelay <= 0) Play(Alert, loop: false);
                    else ClipTime = 0;   // AnimOver waits for the alert clip
                }
                break;
            case "PathFind":
                TurnToward(Target, _turnRate * _speedMul);
                walk = Facing(Yaw) * ClipSpeed(Run);
                break;
            case "TurnToPlayer":
            {
                float a = AngleTo(PlayerPos);
                TurnToward(PlayerPos, _turnRate * _speedMul);
                _turnTime += Step;
                string clip = MathF.Abs(a) < 0.15708f ? Ready : a > 0 ? TurnLeft : TurnRight;
                if (clip != Clip) Play(clip, loop: true);
                break;
            }
            case "Taunt":
            case "Sidestep":
                walk = _stepDir * _stepSpeed;
                break;
            }
            if (Brain.StateName == "Jump" || !Grounded)
            {
                if (!Grounded && _moveClip.Length > 0 && Clip == JumpInto && ClipOver) Play(_moveClip, loop: true);
                _vel.Y -= Gravity * Step;
                _speed = _vel * Step;
            }
            else
            {
                // the landing slide's leftover speed, fading as the clip plays
                if (_vel.Xz.LengthSquared > 1e-6f)
                {
                    float k = MathF.Max(0, 1 - 4 * Step);
                    _vel = new Vector3(_vel.X * k, 0, _vel.Z * k);
                }
                else _vel = Vector3.Zero;
                _speed = (walk + _vel) * Step;
            }
            KeepApart();
        }

        // ours: it stops at the player's side instead of standing inside them (Echoes' bodies collide; MPH's walkers don't
        // push the player, so the step toward them is cut once they touch, a little inside so the hurt volume still
        // overlaps the player's and Contact lands)
        void KeepApart()
        {
            Vector2 to = PlayerPos.Xz - Position.Xz;
            float gap = MathF.Max(_radius, _height / 2) + PlayerEntity.Main.Volume.SphereRadius - 0.25f;
            float d = to.Length;
            if (d < 1e-4f || d > gap + _speed.Xz.Length) return;
            Vector2 n = to / d;
            Vector2 step = _speed.Xz;
            float inward = Vector2.Dot(step, n);
            float room = MathF.Max(0, d - gap);
            if (inward > room) step -= n * (inward - room);
            _speed = new Vector3(step.X, _speed.Y, step.Y);
        }

        // the floor under it: a ray down (MPH's walkers keep to their paths; this one falls and lands)
        void Ground()
        {
            Vector3 next = Position + _speed;
            bool wall = false, ground = false;
            HandleBlockingCollision(next.AddY(_hurtVolumeInit.SpherePosition.Y), _hurtVolume, updateSpeed: true, ref ground, ref wall);
            next = Position + _speed;
            float probe = _vel.Y > 0 ? 0 : 0.3f + MathF.Max(0, -_speed.Y);
            CollisionResult res = default;
            bool hit = probe > 0 && CollisionDetection.CheckBetweenPoints(next.AddY(_height * 0.6f), next.AddY(-probe), TestFlags.None, _scene, ref res)
                && res.Plane.Y > 0.5f;
            if (hit)
            {
                float y = res.Position.Y;
                _speed.Y = y - Position.Y;
                if (!Grounded)
                {
                    Grounded = true;
                    _landed = true;
                    _vel = new Vector3(_vel.X, 0, _vel.Z);
                    float slide = ClipSpeed(Land) * 0.25f;
                    if (_vel.Xz.Length > slide) _vel = new Vector3(_vel.X, 0, _vel.Z).Normalized() * slide;
                    Play(Land, loop: false);
                }
            }
            else if (Grounded && _vel.Y <= 0)
            {
                // walked off a ledge
                Grounded = false;
                _vel = _speed / Step;
            }
            if (Grounded && Brain.StateName != "Jump") _landed = false;
        }

        // touching the player: contact damage, the pounce's own while airborne; once per DamageWaitTime
        void Contact()
        {
            if (_contactCool > 0 || !HitPlayers[PlayerEntity.Main.SlotIndex]) return;
            float dmg = Grounded ? _contact : _attackDamage;
            if (ContactDamagePlayer((uint)Math.Max(1, MathF.Round(dmg)), knockback: !Grounded))
            {
                DamageDealt += dmg;
                _contactCool = _damageWait;
            }
        }
    }
}
