using System;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the Trooper's alt form, the Scout Drone (docs/CHARACTERS_AND_SKINS.md, D10/D10a). Host-guarded: off unless
    // the match host turns it on for a seat (HostScoutDrone, a Trooper seat in a local match) before the match loads; with
    // it off nothing here runs and Weavel's alt form is MphRead's own. The Trooper plays on Weavel, so his split is
    // Weavel's: Morph leaves half his health behind (HalfturretEntity) and he plays the other half; Morph again merges.
    //   - the half left behind is his standing body: it never fires (HalfturretEntity's auto-fire is off for him), stands
    //     where he split, keeps its half health and Weavel's turret hit sphere (hitboxes are sim only, never a model's)
    //   - the half he plays is the drone: Weavel's alt-form walking as is on the ground, and it hovers: hold Jump to rise,
    //     let go to sink back down; never higher than his own head height above the floor under it
    //   - its blasters (owner 2026-10-08), instead of Weavel's lunge: hold the alt attack (fire) and it unfolds its arms
    //     (32 frames, the Prime drone's A_intoshootloop; no shots yet, and once started the unfold always finishes), then
    //     fires while held, at most 2.5 s: both blasters together every 8 frames (its A_shootloop, the projectile event
    //     0.05 s in), from its two top guns (its GUN_TOP locators), where he aims (bots: at their target, a bot's press
    //     holding the trigger for the whole burst while its target lives); let go, or the 2.5 s run out, and it folds
    //     back (24 frames, A_outofshootloop); every fold ends with a 2 s wait before the next unfold. Damage 2 then 3 per
    //     shot by turns (94 when every shot of a full burst lands; Double Damage and the Prime Hunter's 150% apply as to his other shots); its shots have their own
    //     pool, so a burst never cuts short the player's other live shots
    //   - its second attack, the cutting laser (L2), is in PlayerDroneLaser.cs; the burst and the laser each have their own wait
    //   - looks: MPH's Psycho Bit stands in for the Prime 1 pirate drone (the app draws the real drone body when it's
    //     imported, MphRecomp DroneBody/MatchDrones, its arms following DroneFireState; its real beam comes later)
    // Speeds and times here are sim code (60 Hz frames, Speed in units per 30 Hz tick as MphRead's); only sqrt, no trig.
    public partial class PlayerEntity
    {
        // set by the match host for this seat before the match loads (default off: MphRead's own Weavel)
        public bool HostScoutDrone { get; set; }
        // N9 (release test notes, owner 2026-10-10: "Gravity is extra strong when pirate drone hits bounce pad"): a jump pad
        // flies the drone up as MPH flies its alt forms (a longer straight start, then 3x gravity: the arenas' pads are tuned
        // for it, and the biped's arc lets its wider sphere catch Headshot's ledge lips: -trooperfixtest pad), but from the
        // top it comes down at the biped's gravity, its sideways speed cut by sqrt(biped / alt gravity) so it lands where
        // MPH's flight lands, just ~1.8x as slowly; holding Jump on the way down hovers (within its cap). Off: replays from
        // before it (MPH's alt-form flight, no hover till landing).
        public bool HostDronePadArc { get; set; } = true;
        // dev (--es dronetest 1, MatchSession; the launcher's "Drone kill test"): a bot fires its drone whenever it has a live
        // target, not only on the AI's rare alt-attack press (owner 2026-10-17: "We also still need to test drone killing me")
        public static bool HostDroneTest { get; set; }
        private bool DronePadArcOn => HostScoutDrone && HostDronePadArc && IsAltForm;
        // headshots on his standing body (owner 2026-10-15): a hit in the top 0.3 of its biped cylinder is a headshot by the
        // same rules as on any standing hunter (HalfturretEntity.HostBodyHeadshot, BeamProjectileEntity). Off: replays from
        // before it (format < 35: the turret's no headshots).
        public bool HostDroneBodyHeadshots { get; set; } = true;
        private bool _dronePadFall = false; // this pad flight's top is past (its sideways speed already cut)

        private const float _droneRiseSpeed = 0.1f; // Speed.Y while Jump is held (3 units/s)
        private const float _droneSinkSpeed = -0.06f; // Speed.Y when let go in the air
        private const float _droneVAccel = 0.012f; // per frame change of Speed.Y toward the target
        private const float _droneDropAccel = 0.03f; // ... when above the cap (flown off a ledge): back down fast
        private const float _droneDropSpeed = -0.3f; // the fastest it drops back under the cap
        private const float _droneProbeExtra = 4f; // the floor probe looks this far below the cap
        // the blaster burst (frames, 60 per second; the Prime drone's clip lengths)
        private const int _droneUnfoldFrames = 32; // A_intoshootloop_drone, 0.533 s
        private const int _droneFireFrames = 150; // the most it fires per burst, 2.5 s
        private const int _droneFoldFrames = 24; // A_outofshootloop_drone, 0.40 s
        private const int _droneCooldownFrames = 120; // after every fold, 2 s
        private const int _droneVolleyEvery = 8; // A_shootloop_drone, 0.133 s
        private const int _droneVolleyAt = 3; // its projectile event, 0.05 s into the loop: 19 volleys in 150 frames
        private const ushort _droneDamageA = 2; // per shot, even volleys (the first is 0) ...
        private const ushort _droneDamageB = 3; // ... and odd ones: 38 shots, 94 damage when all land
        private const int _droneBeamPool = 40; // a full burst's 38 shots in flight at once
        // its top guns in the aim frame (world units: the body's L/R_GUN_TOP locators in the shoot pose at DroneBody.Scale
        // 0.3, sideways averaged), pulled back to the wall when one is closer
        private const float _droneMuzzleSide = 0.10f;
        private const float _droneMuzzleForward = 0.73f;
        private const float _droneLookScale = 0.45f; // Psycho Bit stand-in, model units -> about a morph ball

        private ModelInstance? _droneModel = null;
        private EquipInfo? _droneEquip = null;
        private BeamProjectileEntity[]? _droneBeams = null;
        private int _droneFireState = 0; // 0 folded, 1 unfolding, 2 firing, 3 folding
        private int _droneFireFrame = 0; // frames into that state
        private int _droneCooldown = 0;
        private int _droneVolley = 0; // volleys this burst
        private bool _droneBotHold = false;
        private float _droneFloorGap = -1; // the drone's bottom above the floor under it, last probe (-1: no floor found)

        // for tests (-dronetest)
        public int DroneShots { get; private set; }
        public int DroneSplits { get; private set; }
        public int DroneBursts { get; private set; }
        public int DroneCooldown => _droneCooldown;
        public bool DroneHeld { get; private set; } // the trigger as the burst read it this frame
        public int[] DroneShotsBySide { get; } = new int[2];
        // the drone's own shot pool (the app draws Prime's look on these; Scout Drone MP1 beam session)
        public BeamProjectileEntity[]? DroneBeams => _droneBeams;

        // the kill message for the drone's blaster shots and its laser (owner 2026-10-17: "change the killed
        // by player (power beam) message to say (sentry drone)"). Text only: the shots still count as Power Beam kills in
        // the stats, as before. A match client gets it from the host's snapshot (PlayerNet NetLastDrone).
        public const string DroneKillName = "SENTRY DRONE";

        private static bool DroneHit(PlayerEntity? attacker, BeamProjectileEntity? beam)
        {
            if (attacker == null)
            {
                return false;
            }
            if (beam == null)
            {
                return _droneLaserApplying;
            }
            return attacker._droneBeams != null && Array.IndexOf(attacker._droneBeams, beam) >= 0;
        }
        public int DroneMuzzleClamps { get; private set; }
        public float DroneFloorGap => _droneFloorGap;
        public float DroneCap => Fixed.ToFloat(Values.MaxPickupHeight) - Fixed.ToFloat(Values.MinPickupHeight);

        // Initialize (spawn): the stand-in look and the blasters' weapon
        private void ScoutDroneInit()
        {
            ScoutDroneBurstReset(cooldown: 0);
            _droneLaserWait = 0;
            _droneFloorGap = -1;
            if (!HostScoutDrone)
            {
                return;
            }
            if (_droneModel == null)
            {
                _droneModel = Read.GetModelInstance(Metadata.EnemyModelNames[23]); // PsychoBit
                _droneModel.SetAnimation(3); // its hover
            }
            _models.Add(_droneModel);
            _droneEquip ??= new EquipInfo();
            _droneEquip.Weapon = Weapons.Current[0]; // Power Beam (the drone's real beam needs the Retro particle reader)
            _droneEquip.InfiniteAmmo = true;
            _droneBeams ??= SceneSetup.CreateBeamList(_droneBeamPool, _scene);
            _droneEquip.Beams = _droneBeams;
            ScoutDroneLaserInit();
        }

        private void ScoutDroneBurstReset(int cooldown)
        {
            _droneFireState = 0;
            _droneFireFrame = 0;
            _droneVolley = 0;
            _droneBotHold = false;
            _droneCooldown = cooldown; // the burst's wait (the laser's is its own: ScoutDroneLaserEnd)
            DroneHeld = false;
            _droneLaserFrame = -1;
            _droneLaserHitting = false;
            _droneBotLaser = false;
        }

        // EnterAltForm, Weavel's split: the spawn flourish where the drone starts
        private void ScoutDroneSplit()
        {
            int effectId = GameState.Multiplayer && PlayerCount > 2 && !Features.MaxPlayerDetail ? 33 : 31;
            _scene.SpawnEffect(effectId, Vector3.UnitX, Vector3.UnitY, Position);
            PlayHunterSfx(HunterSfx.Spawn);
            DroneSplits++;
        }

        // end of EnterAltForm (owner 2026-10-08: "just spawning", not Weavel's flip into the turret): the morph is over at
        // once, as when the biped's Morph clip ends (Process), so his body never plays Weavel's flip
        private void ScoutDroneSplitAtOnce()
        {
            Flags1 &= ~PlayerFlags1.Morphing;
            UpdateForm(altForm: true);
        }

        // end of ExitAltForm: whole again at once (no Weavel flip back), as when the Unmorph clip ends (Process); the
        // drone's spawn flourish where he forms
        private void ScoutDroneMergeAtOnce()
        {
            Flags1 &= ~PlayerFlags1.Unmorphing;
            if (_droneLaserFrame >= 0)
            {
                ScoutDroneLaserEnd(); // merged mid-laser: as if it had ended
            }
            else if (_droneFireState != 0)
            {
                ScoutDroneBurstReset(_droneCooldownFrames); // merged mid-burst: as if it had folded
            }
            if (IsMainPlayer && CameraSequence.Current != null)
            {
                CameraSequence.Current.InitialCamInfo.NodeRef = NodeRef;
            }
            else
            {
                CameraInfo.NodeRef = NodeRef;
            }
            if (_burnTimer > 0)
            {
                CreateBurnEffect();
            }
            SetBipedAnimation(PlayerAnimation.Idle, AnimFlags.None);
            int effectId = GameState.Multiplayer && PlayerCount > 2 && !Features.MaxPlayerDetail ? 33 : 31;
            _scene.SpawnEffect(effectId, Vector3.UnitX, Vector3.UnitY, Position);
        }

        // ProcessMovement, before gravity: the drone's height (true: it set Speed.Y and gravity is skipped this frame).
        // On the floor without Jump it's Weavel's walking legs (false).
        private bool ScoutDroneHover()
        {
            if (!IsAltForm || _health == 0 || _frozenTimer > 0)
            {
                return false;
            }
            bool pad = Flags1.TestFlag(PlayerFlags1.UsedJumpPad);
            if (!pad)
            {
                _dronePadFall = false;
            }
            if (pad && (!HostDronePadArc || _jumpPadControlLock > 0 || Speed.Y > 0))
            {
                return false; // a pad's flight: MPH's climb
            }
            bool rise = Controls.Jump.IsDown;
            if (pad)
            {
                if (!_dronePadFall)
                {
                    // the top: from here it falls at the biped's gravity (PlayerInput), as far sideways as at 3x
                    // (a same-arc pad already flies the biped's arc: nothing to cut)
                    _dronePadFall = true;
                    if (!_jumpPadSameArc)
                    {
                        float cut = MathF.Sqrt(Fixed.ToFloat(Values.BipedGravity) / Fixed.ToFloat(Values.AltAirGravity));
                        Speed = new Vector3(Speed.X * cut, Speed.Y, Speed.Z * cut);
                        _jumpPadAccel = new Vector3(_jumpPadAccel.X * cut, _jumpPadAccel.Y, _jumpPadAccel.Z * cut);
                    }
                }
                if (!rise)
                {
                    return false; // coming down (Jump held: hovers)
                }
            }
            if (!rise && Flags1.TestFlag(PlayerFlags1.Standing))
            {
                _droneFloorGap = 0;
                return false;
            }
            float cap = DroneCap;
            float radius = Fixed.ToFloat(Values.AltColRadius);
            Vector3 center = Position.AddY(Fixed.ToFloat(Values.AltColYPos));
            float bottom = center.Y - radius;
            var result = new CollisionResult();
            _droneFloorGap = -1;
            if (CollisionDetection.CheckBetweenPoints(center, center.AddY(-(radius + cap + _droneProbeExtra)),
                TestFlags.Players, _scene, ref result))
            {
                _droneFloorGap = MathF.Max(0, bottom - result.Position.Y);
            }
            if (pad && (_droneFloorGap < 0 || _droneFloorGap > cap))
            {
                return false; // still coming down from the pad's arc (biped gravity) above the cap
            }
            float target;
            float accel = _droneVAccel;
            if (_droneFloorGap < 0 || _droneFloorGap > cap)
            {
                // above the cap (flown off a ledge, or no floor in reach): drops back under it, faster the higher it is
                float over = _droneFloorGap < 0 ? 1 : _droneFloorGap - cap;
                target = MathF.Max(_droneDropSpeed, _droneSinkSpeed - over * 0.6f);
                accel = _droneDropAccel;
                if (Speed.Y > 0)
                {
                    Speed = Speed.WithY(0); // no rising past it, not even a rise's leftover speed
                }
            }
            else if (rise)
            {
                // ease into the cap: never more than half the gap left per tick
                target = MathF.Min(_droneRiseSpeed, (cap - _droneFloorGap) * 0.5f);
            }
            else
            {
                target = _droneSinkSpeed;
            }
            float diff = Math.Clamp(target - Speed.Y, -accel, accel);
            Speed = Speed.AddY(diff);
            return true;
        }

        // ProcessAlt, in place of Weavel's lunge: the blaster burst (unfold, fire while held, fold, wait)
        private void ScoutDroneFire()
        {
            if (_droneEquip == null)
            {
                return;
            }
            if (_health == 0)
            {
                ScoutDroneBurstReset(_droneCooldown);
                return;
            }
            bool able = !IsMorphing && _frozenTimer == 0;
            // each attack's own wait runs on while the other one runs (owner 2026-10-08: chain them)
            if (_droneCooldown > 0 && _droneFireState == 0)
            {
                _droneCooldown--;
            }
            if (_droneLaserWait > 0 && _droneLaserFrame < 0)
            {
                _droneLaserWait--;
            }
            if (_droneLaserFrame >= 0)
            {
                DroneHeld = false;
                ScoutDroneLaserStep(able);
                return;
            }
            bool held;
            if (IsBot)
            {
                // a bot's press is one frame: it holds the trigger for the whole burst while its target lives, or
                // fires the laser (PlayerDroneLaser.cs)
                if (_droneFireState == 0 && (Controls.AltAttack.IsDown || HostDroneTest && AiData.HostTargetPlayer is PlayerEntity { Health: > 0 }))
                {
                    if (ScoutDroneBotWantsLaser())
                    {
                        _droneBotLaser = true;
                    }
                    else if (_droneCooldown == 0)
                    {
                        _droneBotHold = true;
                    }
                }
                if (AiData.HostTargetPlayer is not PlayerEntity target || target.Health == 0)
                {
                    _droneBotHold = false;
                }
                held = _droneBotHold;
            }
            else
            {
                held = Controls.AltAttack.IsDown;
            }
            held &= able;
            DroneHeld = held;
            if (_droneFireState == 0)
            {
                bool laser = (IsBot ? _droneBotLaser : Controls.Zoom.IsDown) && able && _droneLaserWait == 0;
                _droneBotLaser = false;
                if (laser)
                {
                    DroneHeld = false;
                    ScoutDroneLaserStart();
                }
                else if (held && _droneCooldown == 0)
                {
                    _droneFireState = 1;
                    _droneFireFrame = 0;
                    _droneVolley = 0;
                    _droneBotLaserLast = false;
                    DroneBursts++;
                }
                return;
            }
            if (_droneFireState == 1)
            {
                // the unfold always finishes; it fires only if the trigger is still held then
                if (++_droneFireFrame >= _droneUnfoldFrames)
                {
                    _droneFireState = held ? 2 : 3;
                    _droneFireFrame = 0;
                }
                return;
            }
            if (_droneFireState == 2)
            {
                if (!held)
                {
                    _droneFireState = 3;
                    _droneFireFrame = 0;
                    return;
                }
                if (_droneFireFrame % _droneVolleyEvery == _droneVolleyAt)
                {
                    ScoutDroneVolley();
                }
                if (++_droneFireFrame >= _droneFireFrames)
                {
                    _droneFireState = 3;
                    _droneFireFrame = 0;
                }
                return;
            }
            // folding: the wait starts once it's fully folded
            if (++_droneFireFrame >= _droneFoldFrames)
            {
                ScoutDroneBurstReset(_droneCooldownFrames);
            }
        }

        // both blasters at once, where he aims (bots: at their target)
        private void ScoutDroneVolley()
        {
            if (_droneEquip == null)
            {
                return;
            }
            Vector3 center = Position.AddY(Fixed.ToFloat(Values.AltColYPos));
            Vector3 aim = _gunVec1;
            if (IsBot && AiData.HostTargetPlayer is PlayerEntity target && target.Health > 0)
            {
                aim = target.Position - center;
            }
            if (aim.LengthSquared < 1e-6f)
            {
                aim = new Vector3(_field70, 0, _field74);
            }
            aim = aim.Normalized();
            float hLen = MathF.Sqrt(aim.X * aim.X + aim.Z * aim.Z);
            Vector3 right = hLen < 1e-4f ? new Vector3(_field74, 0, -_field70) : new Vector3(aim.Z / hLen, 0, -aim.X / hLen);
            ushort damage = _droneVolley % 2 == 0 ? _droneDamageA : _droneDamageB;
            _droneEquip.UnchargedDamage = damage;
            _droneEquip.HeadshotDamage = damage;
            _droneEquip.ChargeLevel = 0;
            // as his other shots (PlayerInput): Double Damage, else the Prime Hunter's 150%
            BeamSpawnFlags flags = _doubleDmgTimer > 0 ? BeamSpawnFlags.DoubleDamage
                : IsPrimeHunter ? BeamSpawnFlags.PrimeHunter : BeamSpawnFlags.None;
            bool fired = false;
            for (int i = 0; i < 2; i++)
            {
                Vector3 muzzle = center + right * (i == 0 ? -_droneMuzzleSide : _droneMuzzleSide) + aim * _droneMuzzleForward;
                // never past a wall: from the wall, a hair back toward the drone
                var col = new CollisionResult();
                if (CollisionDetection.CheckBetweenPoints(center, muzzle, TestFlags.Beams, _scene, ref col))
                {
                    Vector3 toWall = col.Position - center;
                    float len = toWall.Length;
                    muzzle = len > 0.05f ? center + toWall * ((len - 0.05f) / len) : center;
                    DroneMuzzleClamps++;
                }
                BeamResultFlags result = BeamProjectileEntity.Spawn(this, _droneEquip, muzzle, aim, flags, NodeRef, _scene);
                if (result != BeamResultFlags.NoSpawn)
                {
                    fired = true;
                    DroneShots++;
                    DroneShotsBySide[i]++;
                }
            }
            _droneVolley++;
            if (fired)
            {
                _timeSinceShot = 0;
                if (!HostOwnDroneShotSfx)
                {
                    _soundSource.PlaySfx(Metadata.BeamSfx[(int)BeamType.PowerBeam, (int)BeamSfx.Shot]);
                }
            }
        }

        // PlayerHud, the main player flying the drone (draw only; owner 2026-10-08: "the drone needs a reticle"): MPH's
        // reticle, which it hides in alt forms, where the blasters aim: on the first wall along the aim from the drone, else
        // this far out
        private const float _droneReticleReach = 40f;

        private void UpdateDroneReticle()
        {
            Vector3 center = DroneDrawCenter;
            Vector3 aim = _gunVec1.LengthSquared > 1e-6f ? _gunVec1.Normalized() : DroneDrawFacing;
            Vector3 at = center + aim * _droneReticleReach;
            var col = new CollisionResult();
            if (CollisionDetection.CheckBetweenPoints(center, at, TestFlags.Beams, _scene, ref col))
            {
                at = col.Position;
            }
            Matrix.ProjectPosition(at, _scene.ViewMatrix, _scene.PerspectiveMatrix, out Vector2 pos);
            pos = Scene.HostWarpScreenPos(pos); // Panini view, as UpdateReticle
            _targetCircleInst.PositionX = MathF.Round(pos.X, 5);
            _targetCircleInst.PositionY = MathF.Round(pos.Y, 5);
            _targetCircleInst.Enabled = true;
            _targetCircleInst.ProcessAnimation(_scene);
        }

        // the burst as the app's native drone body plays it (DroneBody.Animator); draw only
        public int DroneFireState => _droneFireState;
        public int DroneFireFrame => _droneFireFrame;

        // PlayerProcess, with the alt model's frames
        private void ScoutDroneAnimate()
        {
            if (_droneModel != null)
            {
                UpdateAnimFrames(_droneModel);
            }
        }

        // where the drone is drawn (the alt form's collision centre) and the way it faces (the aim, level): the stand-in
        // below and the app's native drone body (MatchDrones) alike; draw only
        public Vector3 DroneDrawCenter => Position.AddY(Fixed.ToFloat(Values.AltColYPos));

        public Vector3 DroneDrawFacing
        {
            get
            {
                // during the laser: where the beam aims (bots: at their target)
                Vector3 aim = DroneLaserFrame >= 0 ? _droneLaserAim : _gunVec1;
                var facing = new Vector3(aim.X, 0, aim.Z);
                if (facing.LengthSquared < 1e-6f)
                {
                    facing = new Vector3(_field70, 0, _field74);
                }
                return facing.LengthSquared < 1e-6f ? Vector3.UnitZ : facing.Normalized();
            }
        }

        // recomp online (MphRecomp Net/ snapshots, Trooper abilities in network matches): the drone's burst and laser as
        // the host has them. A puppet takes them at once (its relayed input starts them a little early or late); the
        // local player runs its own, taking only the laser's random sweep and target distance, so the sweep drawn here
        // is the one the host hits with (damage comes from the host)
        public PlayerNetDrone NetDrone => new PlayerNetDrone
        {
            FireState = (byte)_droneFireState,
            FireFrame = (byte)Math.Clamp(_droneFireFrame, 0, 255),
            Cooldown = (byte)Math.Clamp(_droneCooldown, 0, 255),
            LaserFrame = (byte)(_droneLaserFrame < 0 ? 255 : Math.Clamp(_droneLaserFrame, 0, 254)),
            LaserPattern = (byte)_droneLaserPattern,
            LaserWait = (ushort)Math.Clamp(_droneLaserWait, 0, 65535),
            LaserDist = (byte)Math.Clamp(MathF.Round(_droneLaserDist * 6), 0, 255)
        };

        public void NetApplyDrone(in PlayerNetDrone d, bool pose)
        {
            if (!HostScoutDrone)
            {
                return;
            }
            bool laser = d.LaserFrame != 255;
            if (laser && (pose || _droneLaserFrame >= 0))
            {
                _droneLaserPattern = d.LaserPattern & 3;
                _droneLaserDist = Math.Clamp(d.LaserDist / 6f, _droneLaserTargetMin, _droneLaserTargetMax);
            }
            if (!pose)
            {
                return;
            }
            if (laser)
            {
                _droneLaserFrame = d.LaserFrame;
            }
            else if (_droneLaserFrame >= 0)
            {
                ScoutDroneLaserEnd();
            }
            _droneFireState = Math.Clamp((int)d.FireState, 0, 3);
            _droneFireFrame = d.FireFrame;
            _droneCooldown = d.Cooldown;
            _droneLaserWait = d.LaserWait;
        }

        // frames since the player was last hit (MphRead's damage flash clock), for the native drone body's hit clips
        public ushort DroneTimeSinceDamage => _timeSinceDamage;

        // recomp: the stand-in drone in the seat's look-alike colours (D5, Read.HostExtraRecolors index on the Psycho Bit's
        // model); -1 = its own. Drawing only.
        public int HostDroneLookRecolor { get; set; } = -1;

        // PlayerDraw, in place of Weavel's walking legs: the stand-in drone, facing where it aims
        private void DrawScoutDrone()
        {
            DrawDroneLaser();
            if (_droneModel == null)
            {
                return;
            }
            Vector3 facing = DroneDrawFacing;
            Vector3 center = DroneDrawCenter;
            Matrix4 transform = Matrix4.CreateScale(_droneLookScale) * GetTransformMatrix(facing, Vector3.UnitY, center);
            int recolor = HostDroneLookRecolor >= 0 && HostDroneLookRecolor < _droneModel.Model.Recolors.Count ? HostDroneLookRecolor : 0;
            UpdateTransforms(_droneModel, transform, recolor);
            if ((!IsMainPlayer || !HostOwnMainAlt) && !HostOwnAlt)
            {
                GetDrawItems(_droneModel, _droneModel.Model.Nodes[0], _curAlpha, recolor);
            }
        }

        public partial class PlayerAiData
        {
            // dev HostDroneTest, after the AI's own step: a Trooper bot with a live target splits and stays split, so its
            // drone fires at the target (ScoutDroneFire) as soon as the unfold and waits allow
            private void DroneTestStep()
            {
                if (!HostDroneTest || !_player.HostScoutDrone || _player.IsMorphing) return;
                if (HostTargetPlayer is not PlayerEntity { Health: > 0 }) return;
                _touchButtons.Unmorph.IsDown = false;
                if (!_player.IsAltForm && _touchButtons.Morph.FramesUp > 20) _touchButtons.Morph.IsDown = true;
            }
        }
    }

    // one seat's Scout Drone in an online snapshot (MphRecomp Net/; 8 bytes on the wire)
    public struct PlayerNetDrone
    {
        public byte FireState; // 0 folded, 1 unfolding, 2 firing, 3 folding
        public byte FireFrame;
        public byte Cooldown; // the burst's wait (120 frames)
        public byte LaserFrame; // frames into the laser, 255 = off
        public byte LaserPattern; // its sweep, 0-3
        public ushort LaserWait; // the laser's own wait (600 frames)
        public byte LaserDist; // the target's distance along the aim x 6
    }
}
