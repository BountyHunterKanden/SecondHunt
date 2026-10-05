using System;
using MphRead;
using MphRead.Entities;
using MphRecomp.Game;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // One controller frame for a hunter dummy. Sticks in [-1,1]; MoveY +1 = forward.
    public struct DummyInput
    {
        public float MoveX, MoveY;   // left stick
        public float TurnX, AimY;    // right stick: yaw rate, aim pitch rate
        public bool Jump, Fire;      // Fire = the fire button went down this tick
        public bool FireHeld;        // the fire button is down this tick (builds a Power Beam charge)
    }

    // A standing hunter moved by the game's own biped movement rules and animated by BipedAnimator --
    // the "third-person body" used by the animation viewer (and the basis for bots/other players later).
    //
    // Movement is a port of MphRead's biped path (PlayerInput.ProcessBiped/ProcessMovement), limited to
    // flat ground: per-hunter traction + speed caps, the standing/walking/strafing/air speed factors,
    // biped gravity, jump speed, and the 16-tick "still grounded" coyote window -- all read from the
    // hunter's real PlayerValues table. Integration runs at 60 Hz like MphRead (speed is in DS
    // per-30Hz-frame units: Speed += gravity/2, Position += Speed/2 each tick). Omitted: slopes/slip
    // terrain, jump pads, boost, knockback, Spire climbing.
    //
    // Position is the GAME's player position (0.5 above the feet for every hunter; see HunterRig).
    public sealed class HunterDummy
    {
        public Hunter Hunter { get; }
        public BipedAnimator Anim { get; }
        public ICollision? Collision;

        public Vector3 Position, PrevPosition;
        public float Yaw, PrevYaw;           // facing = (sin, 0, -cos), the renderer's convention
        public float AimFacingY;             // spine aim pitch: Y of the unit aim vector
        public Vector3 Speed;                // DS units per 30 Hz frame
        public bool Standing { get; private set; } = true;
        public bool Grounded { get; private set; } = true;
        // Diagnostics for leaving the playable space: the last spot it stood on, and the first spot
        // (this airborne stretch) where there was no floor anywhere below it at all.
        public Vector3 LastStanding;
        public Vector3? NoFloorSince;
        public Vector3 Home; public float HomeYaw; // where it (re)spawns: set by SnapToFloor at placement

        // Out-of-bounds recovery (the game's equivalent is dying to a death plane and respawning).
        public void Respawn()
        {
            Position = PrevPosition = Home; Yaw = PrevYaw = HomeYaw;
            Speed = Vector3.Zero; NoFloorSince = null; LastStanding = Home; _chargeLevel = 0;
        }
        bool _usedJump;
        int _timeSinceGrounded;
        int _chargeLevel; // the game's EquipInfo.ChargeLevel, in 60 Hz ticks

        // Power Beam charge, as PlayerInput: holding fire raises ChargeLevel each tick up to FullCharge; the
        // Charge clip plays once it is above 0; letting go at MinCharge or more fires the charged shot
        // (ChargeShoot). Both clips are torso-only -- the legs keep walking or standing.
        static readonly int MinChargeTicks = Weapons.WeaponsMP[0].MinCharge * 2;   // 18 DS frames = 0.6 s
        static readonly int FullChargeTicks = Weapons.WeaponsMP[0].FullCharge * 2; // 30 DS frames = 1.0 s

        // full right-stick deflection turns 4.5 deg/tick (270 deg/s): past the game's 3 deg/tick Turn
        // threshold, so a hard flick plays Turn and a gentle one doesn't -- the same split a DS stylus had.
        public float TurnDegPerTick = 4.5f;

        readonly float _walkTraction, _strafeTraction, _walkCap, _strafeCap, _gravity, _jumpSpeed,
            _walkFactor, _strafeFactor, _airFactor, _standFactor, _radius;

        public HunterDummy(Hunter hunter, Model model, Vector3 position, float yaw)
        {
            Hunter = hunter;
            Anim = new BipedAnimator(model);
            Position = PrevPosition = position;
            Yaw = PrevYaw = yaw;
            PlayerValues v = Metadata.PlayerValues[(int)hunter];
            _walkTraction = Fixed.ToFloat(v.WalkBipedTraction);
            _strafeTraction = Fixed.ToFloat(v.StrafeBipedTraction);
            _walkCap = Fixed.ToFloat(v.WalkSpeedCap);
            _strafeCap = Fixed.ToFloat(v.StrafeSpeedCap);
            _gravity = Fixed.ToFloat(v.BipedGravity);
            _jumpSpeed = Fixed.ToFloat(v.JumpSpeed);
            _walkFactor = Fixed.ToFloat(v.WalkSpeedFactor);
            _strafeFactor = Fixed.ToFloat(v.StrafeSpeedFactor);
            _airFactor = Fixed.ToFloat(v.AirSpeedFactor);
            _standFactor = Fixed.ToFloat(v.StandSpeedFactor);
            _radius = Fixed.ToFloat(v.BipedColRadius);
        }

        public Vector3 Facing => new(MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        float FeetOffset => HunterRig.PositionYForFeet(Hunter, 0f); // position.Y - feet.Y

        public Vector3 Feet => Position - new Vector3(0, FeetOffset, 0);

        // Drop onto the floor below (spawn placement).
        public void SnapToFloor()
        {
            if (Collision == null) return;
            float f = Collision.FloorBelow(ToN(Feet + new Vector3(0, 0.6f, 0)));
            if (!float.IsNegativeInfinity(f)) { Position.Y = f + FeetOffset; PrevPosition = Position; LastStanding = Position; }
            Home = Position; HomeYaw = Yaw;
        }

        // One 60 Hz tick.
        public void Tick(in DummyInput input)
        {
            PrevPosition = Position; PrevYaw = Yaw;
            const float dz = 0.38f; // stick -> digital D-pad directions
            bool fwd = input.MoveY > dz, back = input.MoveY < -dz, right = input.MoveX > dz, left = input.MoveX < -dz;
            bool wasGrounded = Grounded;

            // aim: stick right turns right. Yaw grows clockwise seen from above (facing = sin, 0, -cos),
            // while the game's aim delta is positive for LEFT turns -- hence the sign flip in the intent.
            Yaw = PrevYaw + MathHelper.DegreesToRadians(input.TurnX * TurnDegPerTick);
            AimFacingY = Math.Clamp(AimFacingY + input.AimY * 0.02f, -0.95f, 0.95f);

            // --- ProcessBiped: movement intent ---
            Vector3 f = Facing;
            var speedDelta = Vector3.Zero;
            bool strafing = false, walking = false;
            if (right || left)
            {
                int sign = right ? 1 : -1;
                strafing = true; walking = Standing;
                // game: speedDelta -= (_field78, _field7C) * traction * sign, _field78 = f.z, _field7C = -f.x
                speedDelta.X -= f.Z * _strafeTraction * sign;
                speedDelta.Z -= -f.X * _strafeTraction * sign;
            }
            if (fwd || back)
            {
                int sign = fwd ? 1 : -1;
                walking = Standing;
                speedDelta.X += f.X * _walkTraction * sign;
                speedDelta.Z += f.Z * _walkTraction * sign;
            }
            bool jumped = false;
            if (input.Jump && !_usedJump && Grounded)
            {
                jumped = true;
                _usedJump = true;
                Speed.Y = _jumpSpeed;
                _timeSinceGrounded = 8 * 2;
            }

            // speed cap (only limits growth, like the game)
            float hCap = strafing && !(fwd || back) ? _strafeCap : _walkCap;
            float magBefore = MathF.Sqrt(Speed.X * Speed.X + Speed.Z * Speed.Z);
            Speed += speedDelta;
            float magAfter = MathF.Sqrt(Speed.X * Speed.X + Speed.Z * Speed.Z);
            if (magAfter > magBefore && magAfter > hCap)
            {
                float factor = magBefore <= hCap ? hCap / magAfter : magBefore / magAfter;
                Speed.X *= factor; Speed.Z *= factor;
            }

            // --- ProcessMovement: friction, gravity, integrate ---
            float speedFactor = Standing ? (strafing ? _strafeFactor : walking ? _walkFactor : _standFactor) : _airFactor;
            Speed.X += (Speed.X * speedFactor - Speed.X) / 2;
            Speed.Z += (Speed.Z * speedFactor - Speed.Z) / 2;
            // the game zeroes gravity while Standing (last tick's collision result) -- including the tick
            // a jump launches, so the full jump speed applies for that first step
            if (!Standing) Speed.Y += _gravity / 2;
            Vector3 step = Speed / 2;

            // --- collision: walls (per axis, slide), then floor ---
            if (Collision != null)
            {
                Vector3 body = Position + new Vector3(0, 0.4f, 0);
                if (step.X != 0 && Collision.Blocked(ToN(body), new System.Numerics.Vector3(MathF.Sign(step.X), 0, 0), MathF.Abs(step.X) + _radius))
                { step.X = 0; Speed.X = 0; }
                if (step.Z != 0 && Collision.Blocked(ToN(body), new System.Numerics.Vector3(0, 0, MathF.Sign(step.Z)), MathF.Abs(step.Z) + _radius))
                { step.Z = 0; Speed.Z = 0; }
            }
            Position += step;

            bool landed = false;
            Standing = false;
            if (Collision != null)
            {
                float floor = Collision.FloorBelow(ToN(Feet + new Vector3(0, 0.6f, 0)));
                if (!float.IsNegativeInfinity(floor) && Feet.Y <= floor + 0.02f && Speed.Y <= 0)
                {
                    Position.Y = floor + FeetOffset;
                    Speed.Y = 0;
                    Standing = true;
                    if (!wasGrounded) landed = true;
                    _usedJump = false;
                    LastStanding = Position;
                    NoFloorSince = null;
                }
                else if (float.IsNegativeInfinity(floor) && NoFloorSince == null) NoFloorSince = Position;
            }
            else if (Position.Y <= PrevPosition.Y && Speed.Y <= 0) { Standing = true; Speed.Y = 0; }

            // grounded = standing, or airborne for fewer than 16 ticks (a jump starts that at 16)
            if (Standing) { _timeSinceGrounded = 0; Grounded = true; }
            else if (_timeSinceGrounded < 90 * 2)
            {
                _timeSinceGrounded++;
                if (_timeSinceGrounded >= 8 * 2) Grounded = false;
            }

            bool charging = false, chargeReleased = false;
            if (input.FireHeld)
            {
                charging = _chargeLevel > 0;
                if (_chargeLevel < FullChargeTicks) _chargeLevel++;
            }
            else if (_chargeLevel > 0)
            {
                chargeReleased = _chargeLevel >= MinChargeTicks;
                _chargeLevel = 0;
            }

            Anim.Tick(new BipedIntent
            {
                Forward = fwd, Back = back, Left = left, Right = right,
                AimYawDeltaDeg = -input.TurnX * TurnDegPerTick, // positive stick = turn right = negative game delta
                Grounded = wasGrounded, // ProcessBiped reads the flag before this tick's movement
                JumpStarted = jumped,
                Landed = landed,
                Fired = input.Fire,
                Charging = charging,
                ChargeReleased = chargeReleased,
                HasInput = fwd || back || left || right || input.Jump || input.Fire || input.FireHeld
                    || MathF.Abs(input.TurnX) > 0.15f || MathF.Abs(input.AimY) > 0.15f,
                TimeSinceJumpPad = 1000,
            });
        }

        static System.Numerics.Vector3 ToN(Vector3 v) => new(v.X, v.Y, v.Z);
    }
}
