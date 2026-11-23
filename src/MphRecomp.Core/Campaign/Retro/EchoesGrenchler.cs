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
    // Echoes' Grenchler (GRCH, the second enemy of docs/ENEMY_IMPORT.md E1) as an MPH enemy, built like EchoesSplinter:
    // its own AI state machine (state.fsm2, FSM2 0e17fe4b, run by Fsm2Runner) with its triggers and states doing what
    // the game's Grenchler code does (docs/enemies/GRENCHLER_RSO.md), every number read from the imported enemy.cfg (the
    // disc's reference placement 1002007f), its clips from the imported body. A lone Grenchler on land: Lurk -> spots
    // you -> turns to face you -> Pursue (runs at you), and from Pursue by range: the bite up close, the electric beam
    // at mid range, the charge from further off, a pause after each; the tail (TailDestroyedHealth) can be knocked off.
    //
    // Not ported yet (their triggers are false, so the machine never goes there): the grapple states (18-34, a Grapple
    // Guardian's), water (Submerged, EmergedFromWater), jumps (no Echoes nav targets in MPH rooms), attack patterns.
    // Ours, as Splinter's: the straight-line path, gravity, the hit pause, the beam -> Echoes weapon map, health x3.
    public sealed class EchoesGrenchler : EnemyInstanceEntity
    {
        const float Unit = EchoesSplinter.Unit;
        public const float HealthScale = EchoesSplinter.HealthScale;
        const float Gravity = EchoesSplinter.Gravity;
        const float Step = EchoesSplinter.Step;

        // the enemy.cfg property paths (Patterned b3774750 and the Grenchler's own; docs/ENEMY_IMPORT.md E1)
        const string P = "b3774750/";
        const string PSpeed = P + "6392404e", PTurn = P + "020c78bb", PDetect = P + "8db77ee4", PDetectAngle = P + "83dfc40f",
            PLeash = P + "3fae47eb", PContact = P + "d756416e/f2d02613", PDamageWait = P + "e0cdc7e3",
            PHealth = P + "cf90d15e/f0668919", PRadius = P + "8a6ab139", PHeight = P + "3011b5df",
            PVuln = P + "7b71ae90/", PVulnValue = "/852d3871", PDamage = "/f2d02613", PKnock = "/555ff80a",
            PTailHealth = "04d51e3a", PTailVuln = "0a7326a3/",
            PChargeWait = "5d8f2bee", PChargeMin = "ea4b88c8", PChargeMax = "aa04f0be",
            PBiteMin = "98d5d373", PBiteMax = "d89aab05", PBitePauseMin = "2e6096ee", PBitePauseMax = "6e2fee98",
            PBiteRadius = "49632b31", PBite = "df636c4b",
            PBeamMin = "262b2508", PBeamMax = "66645d7e", PBeamPauseMin = "909e6095", PBeamPauseMax = "d0d118e3",
            PBeam = "13e30e4d", PBeamAngle = "680ce795",
            PBurstMin = "06f7ceed", PBurstMax = "46b8b69b", PBurstPauseMin = "b0428b70", PBurstPauseMax = "f00dfe06",
            PBurstRadius = "b00775e6", PBurst = "5285db00";

        public const string Ready = "B_ready_grenchler", Run = "B_run_grenchler", Walk = "B_walk_grenchler",
            Sniff = "B_sniff_grenchler", Alert = "B_alert_grenchler", TurnLeft = "B_turn_left_grenchler",
            TurnRight = "B_turn_right_grenchler", Bite = "A_bite_grenchler", Beam = "A_beam_attack_grenchler",
            Flinch = "A_flinch_grenchler", Shake = "B_shake_grenchler", KnockOff = "R_knock_off_shell_grenchler",
            Backstep = "B_step_back_mega_ing_grenchler", Death = "R_land_death_grenchler",
            HitHeavy = "R_heavy_hitfront_grenchler", HitLight = "R_light_hitfront_grenchler";

        public readonly RetroEnemyConfig Config;
        public readonly Fsm2Runner Brain;
        public string BodyDir => Path.Combine(Config.Dir, EnemyImportKind.BodyDir);

        // the draw's view of it (as EchoesSplinter's)
        public string Clip { get; private set; } = Ready;
        public int ClipSerial { get; private set; }
        public bool ClipLoop { get; private set; } = true;
        public float ClipTime { get; private set; }
        public float Yaw { get; private set; }
        public bool Dying { get; private set; }
        public float Hp { get; private set; }
        public float HpMax { get; private set; }
        public bool TailIntact { get; private set; } = true;
        public float TailHp { get; private set; }
        public bool Grounded { get; private set; } = true;
        public int Hits { get; private set; }
        public int Bites { get; private set; }
        public int Beams { get; private set; }
        public int Charges { get; private set; }
        public int Bursts { get; private set; }
        public float DamageDealt { get; private set; }
        public bool HitFlash => _timeSinceDamage < 5 * 2;
        public readonly List<string> Visited = new();

        readonly float _speedMul, _turnRate, _detect, _detectCos, _leash, _contact, _damageWait, _radius, _height,
            _chargeWait, _chargeMin, _chargeMax, _biteMin, _biteMax, _bitePauseMin, _bitePauseMax, _biteRadius, _biteDamage,
            _beamMin, _beamMax, _beamPauseMin, _beamPauseMax, _beamDamage, _beamKnock, _beamCos,
            _burstMin, _burstMax, _burstPauseMin, _burstPauseMax, _burstRadius, _burstDamage, _burstKnock;
        readonly Vector3 _home;
        readonly Random _rng;

        // trigger state
        bool _canSee, _attacked, _hitLanded;
        float _now, _seeTimer, _turnTime, _stuckTime, _hurtTime, _contactCool, _fidget, _deathTime, _sinceSeen,
            _pauseEnd, _hitAt = -1000, _biteEnd = -1000, _beamEnd = -1000, _chargeEnd = -1000,
            _pursueTime, _boredAt, _chargeHp;
        // the RSO's action history (0xa78), newest last: "bite" / "beam" / "burst" / "charge" / "backstep" / "taunt"
        readonly List<string> _history = new();
        string Last => _history.Count > 0 ? _history[^1] : "";
        Vector3 _vel;
        Vector3 _chargeStart, _chargeTarget, _maneuverGoal;

        static readonly Dictionary<string, Dictionary<string, float>> _durations = new(StringComparer.OrdinalIgnoreCase);

        public EchoesGrenchler(int id, RetroEnemyConfig config, Vector3 position, float yaw, Scene scene, int seed = 0, EntityBase? owner = null)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, owner!), NodeRef.None, scene)
        {
            if (config.Fsm == null) throw new InvalidDataException($"{config.Dir}: no {EnemyImportKind.FsmFile}");
            Id = id;
            Config = config;
            Position = _home = position;
            Yaw = yaw;
            _rng = new Random(seed != 0 ? seed : id * 7919 + 23);
            _speedMul = config.F32(PSpeed, 1);
            _turnRate = MathHelper.DegreesToRadians(config.F32(PTurn, 90));
            _detect = config.F32(PDetect, 30) * Unit;
            _detectCos = MathF.Cos(MathHelper.DegreesToRadians(config.F32(PDetectAngle, 60)) / 2);
            _leash = config.F32(PLeash, 100) * Unit;
            _contact = config.F32(PContact, 5);
            _damageWait = config.F32(PDamageWait, 0.5f);
            _radius = config.F32(PRadius, 1.6f) * Unit;
            _height = config.F32(PHeight, 2.5f) * Unit;
            _chargeWait = config.F32(PChargeWait, 4.5f);
            _chargeMin = config.F32(PChargeMin, 8) * Unit;
            _chargeMax = config.F32(PChargeMax, 40) * Unit;
            _biteMin = config.F32(PBiteMin, 2) * Unit;
            _biteMax = config.F32(PBiteMax, 8) * Unit;
            _bitePauseMin = config.F32(PBitePauseMin, 2);
            _bitePauseMax = config.F32(PBitePauseMax, 3);
            _biteRadius = config.F32(PBiteRadius, 6) * Unit;
            _biteDamage = config.F32(PBite + PDamage, 20);
            _beamMin = config.F32(PBeamMin, 9) * Unit;
            _beamMax = config.F32(PBeamMax, 25) * Unit;
            _beamPauseMin = config.F32(PBeamPauseMin, 1);
            _beamPauseMax = config.F32(PBeamPauseMax, 1.5f);
            _beamDamage = config.F32(PBeam + PDamage, 10);
            _beamKnock = config.F32(PBeam + PKnock, 5);
            _beamCos = MathF.Cos(MathHelper.DegreesToRadians(config.F32(PBeamAngle, 45)));
            _burstMin = config.F32(PBurstMin, 0.5f) * Unit;
            _burstMax = config.F32(PBurstMax, 8) * Unit;
            _burstPauseMin = config.F32(PBurstPauseMin, 1);
            _burstPauseMax = config.F32(PBurstPauseMax, 1.5f);
            _burstRadius = config.F32(PBurstRadius, 20) * Unit;
            _burstDamage = config.F32(PBurst + PDamage, 10);
            _burstKnock = config.F32(PBurst + PKnock, 10);
            HpMax = Hp = config.F32(PHealth, 200) * HealthScale;
            TailHp = config.F32(PTailHealth, 80) * HealthScale;
            Brain = new Fsm2Runner(config.Fsm) { Condition = Holds, Entered = Enter, Do = DoAction };
        }

        // a weapon's vulnerability (100 = normal), from the disc: the body's, or the tail's while it is on
        public float Vulnerability(string weapon) => Config.F32(PVuln + weapon + PVulnValue, 100);
        public float TailVulnerability(string weapon) => Config.F32(PTailVuln + weapon + PVulnValue, 100);

        public static Matrix4 ModelMatrix(NativeBody body, Vector3 position, float yaw) => EchoesSplinter.ModelMatrix(body, position, yaw);

        public void ScaleHealth(float mul)
        {
            HpMax *= mul;
            Hp *= mul;
            TailHp *= mul;
            _health = _healthMax = (ushort)Math.Clamp(MathF.Ceiling(Hp), 1, UInt16.MaxValue);
        }

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
            // the hit sphere sits on the feet: its radius (1.6) is over half its height (2.5). Walls are pushed with a
            // smaller sphere inside the body (see Ground), clear of the floor's small steps and of things over its head
            float r = Math.Max(_radius, _height / 2);
            _hurtVolumeInit = new CollisionVolume(new Vector3(0, r + 0.05f, 0), r);
            _blockRadius = Math.Min(_radius, (_height - StepUp) / 2);
            Face();
            _hurtVolume = CollisionVolume.Transform(_hurtVolumeInit, Transform);
            NodeRef = PickupPlacement.NodeAt(_scene, Position);
            // state 0 of 0e17fe4b is Dead: the machine begins at Start
            Brain.Start(Math.Max(Brain.Find("Start"), 0));
        }

        public override bool GetTargetable() => !Dying && base.GetTargetable();

        void Face() => SetTransform(EchoesSplinter.Facing(Yaw), Vector3.UnitY, Position);

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

        float ClipSpeed(string clip) => clip switch
        {
            // body.cfg's travel speeds (m/s), as MPH units
            Run => 14.0359f, Walk => 8.0917f, Bite => 2.3438f, Backstep => 4.065f, _ => 0,
        } * Unit * _speedMul;

        // ---- the state machine's triggers ----

        Vector3 PlayerPos => PlayerEntity.Main.Position;
        float PlayerDist => (PlayerPos - Position).Length;

        float AngleTo(Vector3 to)
        {
            Vector3 d = to - Position;
            if (d.Xz.LengthSquared < 1e-8f) return 0;
            return Wrap(MathF.Atan2(d.X, d.Z) - Yaw);
        }

        static float Wrap(float a)
        {
            while (a > MathF.PI) a -= MathF.Tau;
            while (a < -MathF.PI) a += MathF.Tau;
            return a;
        }

        bool InRange(float min, float max)
        {
            float d = PlayerDist;
            return d >= min && d <= max;
        }

        bool ClearPath()
        {
            CollisionResult res = default;
            return !CollisionDetection.CheckBetweenPoints(Position.AddY(_height * 0.5f), PlayerPos.AddY(0.8f), TestFlags.None, _scene, ref res);
        }

        bool Facing(float degrees) => MathF.Abs(AngleTo(PlayerPos)) <= MathHelper.DegreesToRadians(degrees);

        // the triggers as the RSO's (docs/enemies/GRENCHLER_RSO.md); exact < / <= edges are read by hand
        bool Holds(string name, float arg)
        {
            switch (name)
            {
            case "InDetectionRange": return PlayerDist < _detect && PlayerEntity.Main.Health > 0;
            case "SpotPlayer": return _canSee;
            case "Attacked": return _attacked;
            case "AnimOver": return ClipOver;
            case "Delay": return Brain.TimeInState >= arg;
            case "Leash": return (Position - _home).Length > _leash;
            case "Stuck": return _stuckTime > 0.5f;
            case "TailIntact": return TailIntact;
            case "ClearPathToPlayer": return ClearPath();
            // within 35 deg; behind = not within 90
            case "FacingPlayer": return Facing(35);
            case "PlayerBehindMe": return !Facing(90);
            case "TooMuchTurning": return _turnTime > 2;
            // hit in the last 0.2 s with the player off to the side (not within 91 deg; tailless 96)
            case "JustHit": return _now < _hitAt + 0.2f && !Facing(TailIntact ? 91 : 96);
            // health under the snapshot the charge took
            case "TookDamage": return Hp < _chargeHp;
            case "InBiteRange":
            {
                Vector3 d = PlayerPos - Position;
                float flat = d.Xz.Length;
                return MathF.Abs(d.Y) <= (TailIntact ? 3.5f : 7) * Unit && flat >= _biteMin && flat <= _biteMax;
            }
            case "CanBite": return true;
            case "JustBiteAttacked": return Last == "bite" && _now < _biteEnd + 0.2f;
            // the beam reaches twice as far once it has pursued over 2 s
            case "InBeamRange": return InRange(_beamMin, _pursueTime > 2 ? _beamMax * 2 : _beamMax);
            // tail on, under 3 beams in the history
            case "CanBeamAttack": return TailIntact && Count("beam") < 3;
            case "JustBeamAttacked": return Last == "beam" && _now < _beamEnd + 0.2f;
            case "InBurstRange": return InRange(_burstMin, _burstMax);
            // the burst is the water attack (no water here)
            case "CanBurstAttack": return false;
            case "InChargeRange": return InRange(_chargeMin, _chargeMax);
            case "CanCharge": return _now > _chargeEnd + _chargeWait;
            // 0.9 of the way from where it started to where the player was
            case "ChargeFinished": return (Position - _chargeStart).Xz.Length > 0.9f * (_chargeTarget - _chargeStart).Xz.Length;
            case "AbortCharge": return !TailIntact && (!Facing(91) || Holds("ChargeFinished", 0));
            case "PauseOver": return _now > _pauseEnd;
            case "Bored": return TailIntact && _pursueTime > _boredAt;
            case "ManeuverDone": return !TailIntact || (Position - _maneuverGoal).Xz.Length < 0.5f || Brain.TimeInState > 3;
            case "CancelManeuvering": return Brain.TimeInState > 6;
            case "ShouldBackstep": return false;
            }
            // not ported (grapple, water, jumps, patterns, alerts, patrol paths): never holds
            return false;
        }

        int Count(string action)
        {
            int n = 0;
            foreach (string a in _history) if (a == action) n++;
            return n;
        }

        void Did(string action)
        {
            _history.Add(action);
            if (_history.Count > 4) _history.RemoveAt(0);
        }

        void DoAction(string name)
        {
            if (name == "SetLastActionAsBeam") Did("beam");
            // PickJumpTarget: MPH rooms have no Echoes jump targets, so HasValidJumpTarget stays false
        }

        void Enter(int from, int to)
        {
            string was = from >= 0 ? Brain.Machine.States[from].Name : "";
            string name = Brain.Machine.States[to].Name;
            if (Visited.Count == 0 || Visited[^1] != name) Visited.Add(name);
            // the state left (its Deactivate)
            switch (was)
            {
            case "BiteAttack": _biteEnd = _now; break;
            case "BeamAttack": _beamEnd = _now; break;
            case "Charge": _chargeEnd = _now; break;
            case "PauseBetweenBites": _biteEnd = -1000; break;
            case "PauseBetweenBeams": _beamEnd = -1000; break;
            }
            _attacked = false;
            _hitLanded = false;
            switch (name)
            {
            case "Lurk":
            case "Patrol":
                Play(Ready, loop: true);
                _fidget = 3 + 6 * (float)_rng.NextDouble();
                break;
            case "Turn":
                _turnTime = 0;
                Play(Ready, loop: true);
                break;
            case "Pursue":
                Play(Run, loop: true);
                _stuckTime = 0;
                _pursueTime = 0;
                _boredAt = 5 + 8 * (float)_rng.NextDouble();
                break;
            case "BiteAttack":
                Play(Bite, loop: false);
                Bites++;
                Did("bite");
                break;
            case "BeamAttack":
                Play(Beam, loop: false);
                Beams++;
                Did("beam");
                break;
            case "BurstAttack":
                Play(Shake, loop: false);
                Bursts++;
                Did("burst");
                break;
            case "Charge":
                Play(Run, loop: true);
                Charges++;
                Did("charge");
                _stuckTime = 0;
                _chargeStart = Position;
                _chargeTarget = PlayerPos;
                _chargeHp = Hp;
                break;
            case "ChargeFailed":
                Play(Shake, loop: false);
                break;
            case "PauseBetweenBites":
                Play(Ready, loop: true);
                _pauseEnd = _now + Lerp(_bitePauseMin, _bitePauseMax, (float)_rng.NextDouble());
                break;
            case "PauseBetweenBeams":
                Play(Ready, loop: true);
                _pauseEnd = _now + Lerp(_beamPauseMin, _beamPauseMax, (float)_rng.NextDouble());
                break;
            case "StopBeamAttack":
                Play(Ready, loop: true);
                break;
            case "Maneuver":
            {
                // a spot off to one side of the player at about the same distance (ours: the RSO's goal pick isn't read)
                Vector3 d = (Position - PlayerPos) with { Y = 0 };
                float side = _rng.Next(2) == 0 ? 0.6f : -0.6f;
                float c = MathF.Cos(side), s = MathF.Sin(side);
                _maneuverGoal = PlayerPos + new Vector3(d.X * c - d.Z * s, 0, d.X * s + d.Z * c);
                Play(Run, loop: true);
                break;
            }
            case "Taunt":
                Play(Alert, loop: false);
                Did("taunt");
                break;
            case "Backstep":
                Play(Backstep, loop: false);
                Did("backstep");
                break;
            case "ShakeOff":
                Play(Shake, loop: false);
                break;
            case "Dead":
                Play(Death, loop: false);
                break;
            }
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // ---- damage ----

        protected override bool FirstHuntTakeDamage(uint damage, EntityBase? source)
        {
            if (Dying) return true;
            if (source is BeamProjectileEntity own && own.Owner?.Type == EntityType.EnemyInstance) return true;
            string weapon = EchoesSplinter.Family(source);
            float vuln = Vulnerability(weapon) / 100f;
            Effectiveness eff = vuln <= 0 ? Effectiveness.Zero : vuln < 1 ? Effectiveness.Half : vuln > 1 ? Effectiveness.Double : Effectiveness.Normal;
            (source as BeamProjectileEntity)?.SpawnDamageEffect(eff);
            if (vuln <= 0) return true;
            float dmg = damage * vuln;
            Hp -= dmg;
            Hits++;
            _attacked = true;
            _hitAt = _now;
            _timeSinceDamage = 0;
            if (Hp <= 0)
            {
                Hp = 0;
                Die(source);
                return true;
            }
            _health = (ushort)Math.Clamp(MathF.Ceiling(Hp), 1, UInt16.MaxValue);
            if (TailIntact)
            {
                TailHp -= damage * TailVulnerability(weapon) / 100f;
                if (TailHp <= 0)
                {
                    TailHp = 0;
                    TailIntact = false;
                    Play(KnockOff, loop: false);
                    _hurtTime = Duration(KnockOff);
                    return true;
                }
            }
            if (Grounded && Brain.StateName is not ("Charge" or "BiteAttack" or "BeamAttack" or "BurstAttack"))
            {
                Play(HitLight, loop: false);
                _hurtTime = Math.Min(Duration(HitLight), 0.6f);
            }
            return true;
        }

        void Die(EntityBase? source)
        {
            Dying = true;
            Flags |= EnemyFlags.Invincible;
            _health = 1;
            _vel = Vector3.Zero;
            _speed = Vector3.Zero;
            int dead = Brain.Find("Dead");
            if (dead >= 0) Brain.Force(dead);
            else Play(Death, loop: false);
            if (GameState.StorySave.Stats.EnemyKills != UInt32.MaxValue) GameState.StorySave.Stats.EnemyKills++;
            if (GameEvents.Any) GameEvents.Raise("enemy_killed", "mp2:Grenchler", Id, ((source as BeamProjectileEntity)?.Owner as PlayerEntity)?.SlotIndex ?? -1);
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
            _now += Step;
            if (Brain.StateName == "Pursue") _pursueTime += Step;
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
            if (Brain.StateName is "Pursue" or "Charge" or "Maneuver")
            {
                float want = ClipSpeed(Run) * Step;
                float moved = (Position + _speed - before).Xz.Length;
                _stuckTime = moved < want * 0.2f ? _stuckTime + Step : 0;
            }
        }

        void Resume()
        {
            switch (Brain.StateName)
            {
            case "Pursue" or "Charge" or "Maneuver": Play(Run, loop: true); break;
            case "Dead": break;
            default: Play(Ready, loop: true); break;
            }
        }

        // the sight check, every 0.4 s (as EchoesSplinter's)
        void Sense()
        {
            _seeTimer -= Step;
            _sinceSeen += Step;
            if (_seeTimer > 0) return;
            _seeTimer = 0.4f;
            PlayerEntity p = PlayerEntity.Main;
            _canSee = false;
            if (p.Health <= 0) return;
            Vector3 d = p.Position - Position;
            if (d.LengthSquared > _detect * _detect) return;
            Vector3 flat = new Vector3(d.X, 0, d.Z);
            if (flat.LengthSquared > 1e-6f && Vector3.Dot(flat.Normalized(), EchoesSplinter.Facing(Yaw)) < _detectCos - 1e-4f) return;
            _canSee = ClearPath();
            if (_canSee) _sinceSeen = 0;
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
            case "Lurk":
            case "Patrol":
                _fidget -= Step;
                if (Clip != Ready && ClipOver) Play(Ready, loop: true);
                if (_fidget <= 0)
                {
                    Play(Sniff, loop: false);
                    _fidget = 3 + 6 * (float)_rng.NextDouble();
                }
                break;
            case "Turn":
            {
                // well off (over 120 deg) it turns with the clip; nearer, in place at 2.5x
                float a = AngleTo(PlayerPos);
                bool far = MathF.Abs(a) > MathHelper.DegreesToRadians(120);
                TurnToward(PlayerPos, _turnRate * _speedMul * (far ? 1 : 2.5f));
                _turnTime += Step;
                string clip = MathF.Abs(a) < MathHelper.DegreesToRadians(15) ? Ready : a > 0 ? TurnLeft : TurnRight;
                if (clip != Clip) Play(clip, loop: true);
                break;
            }
            case "Pursue":
                TurnToward(PlayerPos, _turnRate * _speedMul);
                walk = EchoesSplinter.Facing(Yaw) * ClipSpeed(Run);
                break;
            case "Charge":
                // at where the player stood when it began
                TurnToward(_chargeTarget, _turnRate * 2.5f);
                walk = EchoesSplinter.Facing(Yaw) * ClipSpeed(Run);
                break;
            case "Maneuver":
                TurnToward(_maneuverGoal, _turnRate * 2);
                walk = EchoesSplinter.Facing(Yaw) * ClipSpeed(Run);
                break;
            case "BiteAttack":
                TurnToward(PlayerPos, _turnRate * 0.5f);
                walk = EchoesSplinter.Facing(Yaw) * ClipSpeed(Bite);
                if (!_hitLanded && ClipTime >= Duration(Bite) * 0.4f)
                {
                    _hitLanded = true;
                    if (PlayerDist < _biteRadius && Vector3.Dot((PlayerPos - Position).Normalized(), EchoesSplinter.Facing(Yaw)) > 0.3f)
                        Hurt(_biteDamage, knockback: false);
                }
                break;
            case "BeamAttack":
                TurnToward(PlayerPos, _turnRate * 0.5f);
                if (!_hitLanded && ClipTime >= Duration(Beam) * 0.4f)
                {
                    _hitLanded = true;
                    Vector3 d = PlayerPos - Position;
                    if (d.Length <= _beamMax * 1.2f && Vector3.Dot(d.Normalized(), EchoesSplinter.Facing(Yaw)) >= _beamCos && ClearPath())
                        Hurt(_beamDamage, knockback: _beamKnock > 0);
                }
                break;
            case "BurstAttack":
                if (!_hitLanded && ClipTime >= Duration(Shake) * 0.4f)
                {
                    _hitLanded = true;
                    if (PlayerDist < _burstRadius && ClearPath()) Hurt(_burstDamage, knockback: _burstKnock > 0);
                }
                break;
            case "PauseBetweenBites":
            case "PauseBetweenBeams":
                TurnToward(PlayerPos, _turnRate);
                break;
            case "Backstep":
                walk = -EchoesSplinter.Facing(Yaw) * ClipSpeed(Backstep);
                break;
            }
            if (!Grounded)
            {
                _vel.Y -= Gravity * Step;
                _speed = _vel * Step;
            }
            else
            {
                _vel = Vector3.Zero;
                _speed = walk * Step;
            }
            KeepApart();
        }

        void Hurt(float dmg, bool knockback)
        {
            if (ContactDamagePlayer((uint)Math.Max(1, MathF.Round(dmg)), knockback))
            {
                DamageDealt += dmg;
                _contactCool = _damageWait;
            }
        }

        // as EchoesSplinter's: stops at the player's side
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

        const float StepUp = 0.35f;   // the floor bump the wall sphere passes over
        float _blockRadius;

        void Ground()
        {
            Vector3 next = Position + _speed;
            bool wall = false, ground = false;
            // walls with the smaller sphere: the hit sphere is the bounding radius the base call reads
            float bounding = _boundingRadius;
            _boundingRadius = _blockRadius;
            HandleBlockingCollision(next.AddY(StepUp + _blockRadius), _hurtVolume, updateSpeed: true, ref ground, ref wall);
            _boundingRadius = bounding;
            next = Position + _speed;
            float probe = _vel.Y > 0 ? 0 : 0.3f + MathF.Max(0, -_speed.Y);
            CollisionResult res = default;
            bool hit = probe > 0 && CollisionDetection.CheckBetweenPoints(next.AddY(_height * 0.6f), next.AddY(-probe), TestFlags.None, _scene, ref res)
                && res.Plane.Y > 0.5f;
            if (hit)
            {
                _speed.Y = res.Position.Y - Position.Y;
                if (!Grounded)
                {
                    Grounded = true;
                    _vel = Vector3.Zero;
                }
            }
            else if (Grounded)
            {
                Grounded = false;
                _vel = _speed / Step;
            }
        }

        // touching the player: contact damage once per DamageWaitTime (the charge's ram knocks back)
        void Contact()
        {
            if (_contactCool > 0 || !HitPlayers[PlayerEntity.Main.SlotIndex]) return;
            Hurt(_contact, knockback: Brain.StateName == "Charge");
        }
    }
}
