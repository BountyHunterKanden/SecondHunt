using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: Shooting Gallery's manned turret and its bomb slot (docs/SHOOTING_GALLERY_TURRET.md; MphRecomp.Core
    // Arenas/EchoesTurret.cs drives both). Host-guarded: nothing here runs unless a turret seats this player or its slot
    // holds them; with neither the player is MphRead's own.
    //   - the seat: the player is held at the turret's seat (Echoes puts the player's feet on its seat locator), with no
    //     moving, jumping, morphing or weapons of their own. Their aim turns the barrel inside its limits: the aim inputs
    //     (mouse, aim stick, gyro, touch, a remote seat's view, a bot's aim) turn it directly, and the move stick as in
    //     Echoes (owner D2, 2026-10-16): left/right turns it at the turret's yaw speed, up/down sets a pitch target the
    //     barrel moves to at its pitch speed and that goes back to centre on release. Shoot and Jump presses are kept
    //     for the turret (HostSeatFire / HostSeatExit) while it's under control; the turret clears them.
    //   - the slot: an alt-form player held at the slot's centre, every step, with no unmorphing (owner 2026-10-15);
    //     rolling and boosting go nowhere. Dark Samus can lay bombs there (owner D3: the Samus rule), though her Hyper
    //     Ball otherwise has none. Every other hunter lays Samus's bomb there in place of their own alt-form attack
    //     (owner 2026-10-16: only while held), with Samus's bomb values; its blast sets the slot off as hers does.
    // Sim code: the trig goes through MphRead's deterministic MathF (replays stay bit-exact).
    public partial class PlayerEntity
    {
        // ---- the turret's seat ----

        public bool HostSeated { get; private set; }
        // the turret's kills (its shots, BeamProjectileEntity.HostTurretShot, and the entry kill, which the turret makes with
        // HostTurretKillApplying on) say (HEAVY TURRET) (owner 2026-10-17: "killed by [player] (heavy turret)"). Text only:
        // the stats are as before. A match client gets it from the host's snapshot (PlayerNet NetLastTurret).
        public const string TurretKillName = "HEAVY TURRET";
        public static bool HostTurretKillApplying { get; set; }

        private static bool TurretHit(BeamProjectileEntity? beam) => beam?.HostTurretShot == true || beam == null && HostTurretKillApplying;
        // where the player's feet are while seated (the turret moves it as its lift rises and falls)
        public Vector3 HostSeatPosition { get; set; }
        // the barrel's turn from its centre (degrees: + = left, the way MphRead's heading grows) and pitch (+ = up)
        public float SeatYaw { get; private set; }
        public float SeatPitch { get; private set; }
        // the turret is under control (Echoes: 2 s after getting in); before and after, the barrel recentres at 4x speed
        public bool HostSeatControl { get; set; }
        // presses kept for the turret, which clears them
        public bool HostSeatFire { get; set; }
        public bool HostSeatExit { get; set; }
        // a seated bot's barrel goal (degrees: yaw as SeatYaw, pitch as SeatPitch), set by the turret's bot brain
        public Vector2? HostSeatBotTarget { get; set; }
        // the turret's bot brain is on (Core: off for replays recorded before it): a seated bot aims by HostSeatBotTarget
        // and only the brain fires and leaves (the AI's own Shoot/Jump presses don't)
        public static bool HostSeatBotBrain { get; set; }

        private float _seatCentre; // the barrel's centre heading (degrees, atan2(x, z))
        private float _seatLeft, _seatRight, _seatUp, _seatDown, _seatYawRate, _seatPitchRate;
        private float _seatAimPitch; // the aim inputs' share of the pitch
        private float _seatStickPitch; // the move stick's share, moving to its target at the pitch speed
        // the turret's camera: this far ahead of the seat along the barrel (MphRead units; Echoes 3 m), set by the turret
        public float HostSeatCamAhead { get; set; }
        private float _seatCamTime; // seconds since seated (the view's slide from the eye)
        // the turret's shell around the seat, set by the turret: a bot's sight line ending on it sees this player
        // (PlayerAi's visibility; the camera sits inside it)
        public EntityBase? HostSeatShell { get; set; }

        // seat the player: feet at position, the barrel centred on centre (degrees heading), turning left/right and
        // up/down at most that far (degrees), at yawRate/pitchRate degrees a second
        public void HostSeatOn(Vector3 position, float centre, float left, float right, float up, float down, float yawRate, float pitchRate)
        {
            HostSeated = true;
            HostSeatPosition = position;
            _seatCentre = centre;
            _seatLeft = left;
            _seatRight = right;
            _seatUp = up;
            _seatDown = down;
            _seatYawRate = yawRate;
            _seatPitchRate = pitchRate;
            SeatYaw = 0;
            SeatPitch = 0;
            _seatCamTime = 0;
            _seatAimPitch = 0;
            _seatStickPitch = 0;
            HostSeatControl = false;
            HostSeatFire = false;
            HostSeatExit = false;
            HostSeatBotTarget = null;
            Speed = Vector3.Zero;
            Position = position;
            EquipInfo.ChargeLevel = 0;
            NetAimEpoch++; // the turret re-aims the player (PlayerNet.cs)
            SeatAim();
        }

        // out of the seat: feet at position, moving at speed (per 30 Hz tick, as Speed)
        public void HostSeatOff(Vector3 position, Vector3 speed)
        {
            if (!HostSeated)
            {
                return;
            }
            HostSeated = false;
            HostSeatShell = null;
            HostSeatControl = false;
            HostSeatFire = false;
            HostSeatExit = false;
            Position = position;
            Speed = speed;
            NetAimEpoch++;
        }

        // ProcessInput while seated, in place of ProcessBiped
        private void SeatInput()
        {
            const float step = 1 / 60f;
            bool alive = _health > 0 && _frozenTimer == 0;
            // the aim inputs, as ProcessBiped reads them (degrees; mouse, gyro, touch and a remote seat's view come
            // through the mouse delta, keys and bots through the button aim)
            float aimX = 0;
            float aimY = 0;
            if (alive && Controls.MouseAim && !Flags1.TestFlag(PlayerFlags1.NoAimInput) && !IsBot)
            {
                aimX = -Input.MouseDeltaX / 4f;
                aimY = -Input.MouseDeltaY / 4f;
                if (MathF.Abs(aimX) > IdleAimDeadZone || MathF.Abs(aimY) > IdleAimDeadZone)
                {
                    Input.HasInput = true;
                }
            }
            if (alive && (Controls.KeyboardAim || IsBot))
            {
                aimX += _buttonAimX;
                aimY += _buttonAimY;
            }
            if (Controls.InvertAimX)
            {
                aimX = -aimX;
            }
            if (Controls.InvertAimY)
            {
                aimY = -aimY;
            }
            if (IsBot && HostSeatBotBrain)
            {
                // a bot's aim in the seat: the turret's bot brain names the barrel's goal (HostSeatBotTarget: yaw, pitch),
                // turned to at the turret's speeds; none: held
                aimX = aimY = 0;
                if (alive && HostSeatBotTarget is Vector2 goal)
                {
                    aimX = Math.Clamp(goal.X - SeatYaw, -_seatYawRate * step, _seatYawRate * step);
                    aimY = Math.Clamp(goal.Y - SeatPitch, -_seatPitchRate * step, _seatPitchRate * step);
                }
            }
            // the move stick, Echoes' own turret controls (a remote seat's view already has it: its device turned the
            // barrel; bots aim with their aim only)
            float stickX = 0;
            float stickY = 0;
            if (alive && HostSeatControl && !HostRemote && !IsBot)
            {
                stickX = (Controls.MoveLeft.IsDown ? 1 : 0) - (Controls.MoveRight.IsDown ? 1 : 0);
                stickY = (Controls.MoveUp.IsDown ? 1 : 0) - (Controls.MoveDown.IsDown ? 1 : 0);
            }
            if (HostSeatControl)
            {
                // the stick's step: Echoes skips one that would pass a limit (the barrel stops short of it, audit 9.1); the
                // aim's (mouse, gyro: ours) is clamped
                float stepped = SeatYaw + stickX * _seatYawRate * step;
                if (stepped >= -_seatRight && stepped <= _seatLeft) SeatYaw = stepped;
                SeatYaw = Math.Clamp(SeatYaw + aimX, -_seatRight, _seatLeft);
                _seatAimPitch = Math.Clamp(_seatAimPitch + aimY, -_seatDown, _seatUp);
                float target = stickY * (stickY > 0 ? _seatUp : _seatDown);
                _seatStickPitch = MoveTo(_seatStickPitch, target, _seatPitchRate * step * PitchEase(target - _seatStickPitch));
                SeatPitch = Math.Clamp(_seatAimPitch + _seatStickPitch, -_seatDown, _seatUp);
            }
            else
            {
                // getting in or out: back to the centre at 4x the turret's speeds
                SeatYaw = MoveTo(SeatYaw, 0, 4 * _seatYawRate * step);
                _seatAimPitch = MoveTo(_seatAimPitch, 0, 4 * _seatPitchRate * step);
                _seatStickPitch = MoveTo(_seatStickPitch, 0, 4 * _seatPitchRate * step);
                SeatPitch = _seatAimPitch + _seatStickPitch;
            }
            if (alive && HostSeatControl && !(IsBot && HostSeatBotBrain)) // (the brain fires and leaves for a bot)
            {
                if (Controls.Shoot.IsPressed)
                {
                    HostSeatFire = true;
                }
                if (Controls.Jump.IsPressed || TouchJumpPressed)
                {
                    HostSeatExit = true;
                }
            }
            Speed = Vector3.Zero;
            Position = HostSeatPosition;
            _volume = CollisionVolume.Move(_volumeUnxf, Position);
            _seatCamTime += step;
            SeatAim();
            UpdateCamera();
            UpdateAimVecs();
        }

        // the manned pitch step's ease (rso 0xeb4-0xefc): 1 - CMath::PhongBlob(|gap| / 90 degrees, 8), where PhongBlob(x, p) =
        // ((1 + cos(pi x)) / 2) ^ p with x clamped to 0..1 (Trilogy DOL 0x803855d0): full speed at a 90 degree gap, slowing as
        // the barrel nears its target (recentring, unmanned, is linear at 4x)
        public static float PitchEase(float gap)
        {
            float x = Math.Clamp(MathF.Abs(gap) / 90, 0, 1);
            return 1 - MathF.Pow((1 + MathF.Cos(MathF.PI * x)) / 2, 8);
        }

        private static float MoveTo(float value, float target, float amount)
        {
            if (MathF.Abs(target - value) <= MathF.Max(amount, 0.1f))
            {
                return target; // (Echoes snaps within 0.1 degrees)
            }
            return value + (target > value ? amount : -amount);
        }

        // the player's view along the barrel: the gun vector, the facing (no easing: the view is the barrel's) and the
        // axes ProcessMovement derives from the facing
        private void SeatAim()
        {
            float heading = MathHelper.DegreesToRadians(_seatCentre + SeatYaw);
            float pitch = MathHelper.DegreesToRadians(SeatPitch);
            float cp = MathF.Cos(pitch);
            _gunVec1 = new Vector3(cp * MathF.Sin(heading), MathF.Sin(pitch), cp * MathF.Cos(heading)).Normalized();
            _aimY = SeatPitch;
            _facingVector = _gunVec1;
            _aimPosition = CameraInfo.Position + _gunVec1 * Fixed.ToFloat(Values.AimDistance);
            float hMag = MathF.Sqrt(_facingVector.X * _facingVector.X + _facingVector.Z * _facingVector.Z);
            _field70 = _facingVector.X / hMag;
            _field74 = _facingVector.Z / hMag;
            _gunVec2 = new Vector3(_field74, 0, -_field70);
            _field78 = _gunVec2.X;
            _field7C = _gunVec2.Z;
            _upVector = Vector3.Cross(_facingVector, _gunVec2).Normalized();
        }

        // the barrel's way (unit), for the turret's shot
        public Vector3 SeatAimVector => _gunVec1;

        // UpdateCamera, before CameraInfo.Update: Echoes' turret camera in first person (Trilogy DOL CPlayer 0x8014d330,
        // the first-person camera 0x8008d1c8): not the eye but a point ahead of the seat locator along the barrel
        // (HostSeatCamAhead; the feet stay on the locator, so no eye height), looking along it; getting in, the view
        // slides there from the eye over 0.5 s (its turn at once)
        private void SeatCamera()
        {
            if (!HostSeated || CameraType != CameraType.First)
            {
                return;
            }
            Vector3 cam = HostSeatPosition + _gunVec1 * HostSeatCamAhead;
            float t = Math.Clamp(_seatCamTime / 0.5f, 0, 1);
            CameraInfo.Position = cam + (CameraInfo.Position - cam) * (1 - t);
            CameraInfo.Target = CameraInfo.Position + _gunVec1;
            CameraInfo.UpVector = Vector3.UnitY;
        }

        // ---- the bomb slot ----

        // held at this ball centre (null: not held)
        public Vector3? HostSlotHold { get; set; }
        public bool HostSlotHeld => HostSlotHold != null;
        // held, and not Samus (Dark Samus included): the alt-form attack lays Samus's bomb instead
        public bool HostSlotBombs => HostSlotHeld && Hunter != Hunter.Samus;

        // ProcessAlt: in the slot, every hunter but Samus lays Samus's bomb at the held ball in place of their own
        // alt-form attack, one per Samus's bomb cooldown (true: the slot took the alt-form input this step)
        private bool SlotBombInput()
        {
            if (!HostSlotBombs || HostSlotHold is not Vector3 centre)
            {
                return false;
            }
            if (Controls.AltAttack.IsPressed && _bombCooldown == 0)
            {
                PlayerValues samus = Metadata.PlayerValues[(int)Hunter.Samus];
                // where Samus's would drop with her ball held there (SpawnBomb: her Position, the ball's centre, less 1000)
                Matrix4 transform = GetTransformMatrix(Vector3.UnitZ, Vector3.UnitY, centre.AddY(Fixed.ToFloat(-1000)));
                BombEntity? bomb = BombEntity.Spawn(this, transform, _scene, BombType.MorphBall);
                if (bomb != null)
                {
                    bomb.NodeRef = NodeRef;
                    bomb.Radius = Fixed.ToFloat(samus.BombRadius);
                    bomb.SelfRadius = Fixed.ToFloat(samus.BombSelfRadius);
                    bomb.Damage = (ushort)samus.BombDamage;
                    bomb.EnemyDamage = (ushort)samus.BombEnemyDamage;
                    _bombCooldown = (ushort)(samus.BombCooldown * 2); // todo: FPS stuff
                    bomb.PlaySpawnSfx();
                }
            }
            return true;
        }

        // the slot's fixed camera (Echoes' CameraHints its ball trigger adds: a fixed transform, blended in over In seconds
        // and back to the ball camera over Out), chosen by the turret as it takes the ball (null: none)
        public (Vector3 Pos, Vector3 Dir, float In, float Out)? HostSlotCamera { get; set; }
        private (Vector3 Pos, Vector3 Dir, float In, float Out) _slotCam;
        private float _slotCamBlend;

        // UpdateCamera, before CameraInfo.Update: the alt camera blended toward the slot's camera while held, and back after
        private void SlotCamera()
        {
            const float step = 1 / 60f;
            if (HostSlotHold != null && HostSlotCamera is { } c)
            {
                _slotCam = c;
                _slotCamBlend = c.In > 0 ? Math.Min(1, _slotCamBlend + step / c.In) : 1;
            }
            else if (_slotCamBlend > 0)
            {
                _slotCamBlend = _slotCam.Out > 0 ? Math.Max(0, _slotCamBlend - step / _slotCam.Out) : 0;
            }
            if (_slotCamBlend <= 0 || !IsAltForm)
            {
                _slotCamBlend = IsAltForm ? _slotCamBlend : 0;
                return;
            }
            float u = _slotCamBlend * _slotCamBlend * (3 - 2 * _slotCamBlend);
            float reach = MathF.Max(1, (CameraInfo.Target - _slotCam.Pos).Length);
            CameraInfo.Position += (_slotCam.Pos - CameraInfo.Position) * u;
            CameraInfo.Target += (_slotCam.Pos + _slotCam.Dir * reach - CameraInfo.Target) * u;
            CameraInfo.UpVector = Vector3.UnitY;
        }

        // the facing the slot took the ball at: the stick doesn't turn a held ball (or swing the alt camera with it)
        private (Vector3 Facing, Vector3 Gun1, Vector3 Gun2, float F70, float F74)? _slotFacing;

        // as the camera updates: the input step's movement moved a held ball by the stick (and its collision against the slot's
        // walls), so it goes back to the hold before the camera follows it (Kanden, Sylux and Weavel's cameras shook)
        private void SlotHoldBall()
        {
            if (HostSlotHold is Vector3 centre)
            {
                Speed = Vector3.Zero;
                Position += centre - _volume.SpherePosition;
                _volume = CollisionVolume.Move(_volumeUnxf, Position);
            }
        }

        // after ProcessInput: hold a held ball where it is held, facing the way it was taken
        private void SlotAfterInput()
        {
            if (HostKnockLockout > 0)
            {
                HostKnockLockout = MathF.Max(0, HostKnockLockout - 1 / 60f);
            }
            else if (_knockRamp < 3)
            {
                _knockRamp++;
            }
            if (HostSlotHold is Vector3)
            {
                SlotHoldBall();
                if (_slotFacing is { } f)
                {
                    _facingVector = f.Facing;
                    _gunVec1 = f.Gun1;
                    _gunVec2 = f.Gun2;
                    _field70 = f.F70;
                    _field74 = f.F74;
                }
                else
                {
                    _slotFacing = (_facingVector, _gunVec1, _gunVec2, _field70, _field74);
                }
            }
            else
            {
                _slotFacing = null;
            }
        }
    
        // ---- the turret shots' knockback ----

        // Echoes' ApplyPlayerKnockBackForce (Trilogy DOL 0x8015c210), the turret working out the kick dv (MphRead units per
        // 30 Hz frame, already a third and a half for a body in the air) and cap: the kick along dir (flat), the new speed
        // at most the larger of cap and the speed before, kept along the new velocity; on foot that speed is scaled by
        // 0.65 + 0.35 |n . axis| (n the new velocity's direction, the axis the facing on the ground and the right in the
        // air); then the timer (SetMinimalAccelerationTimer) cutting a body's own steering, KnockSteer below
        public float HostKnockLockout { get; private set; }
        private int _knockRamp = 3;

        public void HostKnock(Vector3 dir, float dv, float cap, float lockout)
        {
            dir = dir.WithY(0);
            if (dir.LengthSquared < 1e-8f || dv <= 0)
            {
                return;
            }
            dir = dir.Normalized();
            float limit = MathF.Max(cap, Speed.Length);
            Vector3 v = Speed + dir * dv;
            float mag = v.Length;
            float speed = MathF.Min(limit, mag);
            if (speed < 1e-5f)
            {
                Speed = Vector3.Zero;
            }
            else
            {
                Vector3 n = v / mag;
                if (!IsAltForm)
                {
                    float dot = Flags1.TestFlag(PlayerFlags1.Standing)
                        ? n.X * _field70 + n.Z * _field74
                        : n.X * _field74 - n.Z * _field70;
                    speed *= 0.65f + 0.35f * MathF.Abs(dot);
                }
                Speed = n * speed;
            }
            HostKnockLockout = lockout;
            _knockRamp = 0;
        }

        // the share of a body's own (stick) steering: 0.074 while the timer runs, then 0.296, 0.296 and full a frame
        // apart (Echoes' table at CPlayer+0x338, read only on foot; 0x8015cf64, 0x8015ea44)
        private static readonly float[] _knockSteer = { 0.074f, 0.296f, 0.296f, 1 };

        private Vector3 KnockSteerCut(Vector3 speedDelta)
        {
            return _knockRamp < 3 ? speedDelta * _knockSteer[_knockRamp] : speedDelta;
        }

        // ---- a bot's turret errand (the turret's bot brain on foot: Core EchoesTurretBots) ----

        // where a bot goes for the turret: walking to a point (a nav node on her way, the seat's enter box), jumping there
        // (1 a jump, 2 with Echoes' second press), or rolling to one in the ball and laying bombs once there (the bomb slot)
        public readonly struct BotErrand
        {
            public readonly Vector3 To;
            public readonly bool Ball;
            public readonly bool Bomb;
            public readonly int Jump;

            public BotErrand(Vector3 to, bool ball, bool bomb, int jump)
            {
                To = to;
                Ball = ball;
                Bomb = bomb;
                Jump = jump;
            }
        }

        // the bot's errand now (null: her own moves), read once per AI step
        public static Func<PlayerEntity, BotErrand?>? HostBotErrand { get; set; }

        public partial class PlayerAiData
        {
            private int _errandFrames;
            private int _errandJump; // AI steps into an errand's jump (0: none)

            // after the AI's own step (PlayerAi.Process): the errand's moves in place of her own (her aim and shots stay
            // hers; the unstick can still take over)
            private void TurretErrand()
            {
                if (HostBotErrand?.Invoke(_player) is not BotErrand errand)
                {
                    _errandOn = false; // recomp 10-23 (Thread A): the crate step and wall cling read it (PlayerAiCrates.cs)
                    _errandFrames = 0;
                    _errandJump = 0;
                    return;
                }
                _errandOn = true;
                _buttons.X.IsDown = false;
                _buttons.B.IsDown = false;
                _buttons.Y.IsDown = false;
                _buttons.A.IsDown = false;
                _buttons.Up.IsDown = false;
                _buttons.Down.IsDown = false;
                _buttons.Left.IsDown = false;
                _buttons.Right.IsDown = false;
                // both touch buttons switch forms (TrySwitchForms): her own step's Unmorph (CheckUnmorph) undid the
                // errand's ball the step after it formed, over and over
                _touchButtons.Morph.IsDown = false;
                _touchButtons.Unmorph.IsDown = false;
                if (_player.IsAltForm != errand.Ball)
                {
                    _buttons.L.IsDown = false;
                    if (!_player.IsMorphing)
                    {
                        (errand.Ball ? _touchButtons.Morph : _touchButtons.Unmorph).IsDown = true;
                    }
                    return;
                }
                if (errand.Ball)
                {
                    _buttons.R.IsDown = false; // (no boosting past it)
                    Func21418D8(errand.To);
                    // bombs: a press every 20 AI steps
                    _buttons.L.IsDown = errand.Bomb && _errandFrames++ % 20 == 0;
                }
                else
                {
                    // a jump: the press, then (2) a second press near the top; over once she is down again
                    bool grounded = _player.Flags1.TestFlag(PlayerFlags1.Grounded);
                    if (_errandJump > 0)
                    {
                        _errandJump++;
                        _buttons.L.IsDown = _errandJump <= 4 || errand.Jump == 2 && _errandJump >= 11 && _errandJump <= 14;
                        if (_errandJump > 14 && grounded)
                        {
                            _errandJump = 0;
                        }
                    }
                    else
                    {
                        _buttons.L.IsDown = errand.Jump > 0 && grounded;
                        _errandJump = _buttons.L.IsDown ? 1 : 0;
                    }
                    Func2142AE8(errand.To);
                }
            }
        }
}
}
