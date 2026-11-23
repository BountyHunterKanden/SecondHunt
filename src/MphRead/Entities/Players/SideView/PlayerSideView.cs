using System;
using MphRead.Formats;
using MphRead.Formats.Culling;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: side-view mode (owner 2026-11-12, docs/SIDE_VIEW_MODE.md; P0 = Data Shrine on the PC). With SideViewOn the
    // player step runs SideViewInput in place of ProcessInput (the one switch in PlayerProcess.cs): its own copies of
    // ProcessInput, ProcessBiped and ProcessMovement (PlayerInput.cs, copied 2026-11-12), cut down to a biped walking and
    // jumping on the plane x = 0 against a SideViewStage (SideViewStage.cs) instead of the room's collision. Running is
    // along z, the aim goes all the way round on the plane (SideViewAim), the jump is solved to rise SideViewJump. Holding
    // the stance button (the pad's weapon ring button, the mouse's zoom) plants the player: no running, the move stick
    // aims too, and a laser shows where the shot goes (SideViewLaser; the host draws it and the reticle).
    // Left out of the copies: wall cling, the Shield Wall, seats, alt forms and morphing, spider ball, drone, grapple,
    // Echoes and swim gravity, zoom, the scan visor, jump pads' same-arc steering, Quadtroids. With SideViewOn false
    // none of this runs and the game is unchanged.
    public partial class PlayerEntity
    {
        public static bool SideViewOn { get; set; }
        public static SideViewStage? SideStage { get; set; }
        // live tuning (the host's dev keys): the jump's rise in world units, and the run's traction + top speed scale
        public const float SideViewJumpDefault = 4.0f;
        public static float SideViewJump { get; set; } = SideViewJumpDefault;
        public static float SideViewRunScale { get; set; } = 1.0f;
        // the main player's right stick, -1..1 (y + is down), set by the host each frame; aims on the plane
        public static float SideViewStickX { get; set; }
        public static float SideViewStickY { get; set; }
        // the main player's stance button on the pad (the host: the weapon ring's, L2 by default), held
        public static bool SideViewStanceHeld { get; set; }
        public static int SideViewKOs { get; set; }
        // set by the host each frame: the point the room's parts are culled from (the camera's centre, which stays in
        // the main room; RoomEntity), and the camera's frame on the plane (centre z, y and half width, height), which
        // keeps the mouse reticle on screen
        public static Vector3 SideViewCullAt { get; set; }
        public static Vector2 SideViewFrameCentre { get; set; }
        public static Vector2 SideViewFrameHalf { get; set; }

        private bool _svPlaced;
        private float _svFacing = 1; // +1 faces +z (screen right), -1 faces -z
        private float _svElev; // aim elevation from the facing, degrees (about -95..95)
        private Vector2 _svAim = new Vector2(1, 0); // the aim on the plane (z, y), a unit vector, all the way round
        private Vector2 _svCursor = new Vector2(SideViewCursorRadius, 0); // the reticle from the muzzle (z, y)
        private bool _svCursorFree; // the mouse moved the reticle last: it sits where the mouse put it on screen
        private Vector2 _svCursorOff; // the free reticle from the camera frame's centre (z, y)
        private int _svAimHold; // frames since the last aim input (running levels the aim once it's run out)
        private int _svDropTimer; // frames left falling through soft floors
        private bool _svOnSoft;
        private bool _svStance; // planted: no running, the move stick aims, the laser shows
        private Vector3 _svMuzzle; // the shot's start, on the plane (UpdateAimVecs moves _muzzlePos after the step)
        private Vector3 _svLaserEnd; // where the aim meets the room or a player, in the stance
        private bool _svLaserHit; // the laser ends on a player

        private const float SideViewStickAim = 0.3f;
        private const float SideViewStickRest = 0.1f;
        private const float SideViewFlipSin = 0.087f; // the aim turns the player round 5 degrees past straight up or down
        private const float SideViewCursorRadius = 4; // the reticle's distance from the muzzle, world units
        private const float SideViewCursorMin = 0.75f; // a mouse reticle nearer the muzzle than this keeps the last aim
        private const float SideViewMouseScale = 1 / 40f; // reticle travel, world units per mouse unit
        private const float SideViewLaserReach = 40;
        private const int SideViewDropFrames = 12;

        private static float _svJumpFor = Single.NaN;
        private static float _svJumpGravity = Single.NaN;
        private static float _svJumpSpeed;

        // the rise of a standing jump at speed v0: the press frame and the next keep the standing frame's gravity (none),
        // then each 60 Hz frame adds g / 2 and moves speed / 2 + g / 8, as ProcessMovement steps it
        public static float SideViewJumpRise(float v0, float g)
        {
            float y = 0;
            float vy = v0;
            float peak = 0;
            for (int frame = 0; frame < 4000; frame++)
            {
                float step = frame < 2 ? 0 : g / 2;
                vy += step;
                y += vy / 2 + step / 4;
                peak = MathF.Max(peak, y);
                if (frame >= 2 && vy <= 0)
                {
                    break;
                }
            }
            return peak;
        }

        // the jump speed that rises exactly `rise` (binary search on SideViewJumpRise)
        public static float SideViewJumpSpeed(float rise, float g)
        {
            if (g >= 0)
            {
                g = -0.04f;
            }
            float lo = 0;
            float hi = 8;
            for (int i = 0; i < 60; i++)
            {
                float mid = (lo + hi) / 2;
                if (SideViewJumpRise(mid, g) < rise)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            return (lo + hi) / 2;
        }

        private float SideViewJumpV0()
        {
            float g = Fixed.ToFloat(Values.BipedGravity);
            if (_svJumpFor != SideViewJump || _svJumpGravity != g)
            {
                _svJumpFor = SideViewJump;
                _svJumpGravity = g;
                _svJumpSpeed = SideViewJumpSpeed(SideViewJump, g);
            }
            return _svJumpSpeed;
        }

        public float SideViewFacing => _svFacing;
        public float SideViewElevation => _svElev;
        public Vector2 SideViewAimDir => _svAim;
        public bool SideViewStance => _svStance;
        public Vector3 SideViewMuzzle => _svMuzzle;
        public Vector2 SideViewCursor => _svCursor;
        // the reticle on the plane (z, y): the free one on screen where the mouse put it, else out from the muzzle
        public Vector2 SideViewReticle => _svCursorFree ? SideViewFrameCentre + SideViewClampToFrame(_svCursorOff)
            : new Vector2(_svMuzzle.Z, _svMuzzle.Y) + _svCursor;
        public Vector3 SideViewLaserEnd => _svLaserEnd;
        public bool SideViewLaserHit => _svLaserHit;

        // copy of ProcessInput (PlayerInput.cs) for the side view: no shield wall, seat or alt form
        private void SideViewInput()
        {
            TouchTick();
            if (_health > 0)
            {
                if (Flags1.TestFlag(PlayerFlags1.FreeLook))
                {
                    Flags1 |= PlayerFlags1.FreeLookPrevious;
                }
                else
                {
                    Flags1 &= ~PlayerFlags1.FreeLookPrevious;
                }
                Flags1 &= ~PlayerFlags1.FreeLook;
                Flags1 &= ~PlayerFlags1.Walking;
                Flags1 &= ~PlayerFlags1.Strafing;
                if (!IsBot)
                {
                    ProcessTouchInput();
                    if (GameState.Multiplayer && !Flags1.TestFlag(PlayerFlags1.WeaponMenuOpen) && Controls.Pause.IsDown)
                    {
                        _showScoreboard = true;
                    }
                    else
                    {
                        _showScoreboard = false;
                    }
                }
                UpdateParalyzerStun();
                if (_frozenTimer > 0)
                {
                    _frozenTimer--;
                    _timeSinceFrozen = 0;
                    if (_frozenTimer == 0)
                    {
                        _soundSource.PlaySfx(SfxId.SHOTGUN_BREAK_FREEZE);
                        if (IsMainPlayer)
                        {
                            CreateIceBreakEffectGun();
                        }
                        else if (Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson))
                        {
                            int lod = Flags2.TestFlag(PlayerFlags2.Lod1) ? 1 : 0;
                            CreateIceBreakEffectBiped(_bipedModelLods[lod].Model);
                        }
                    }
                }
                if (_frozenGfxTimer > 0)
                {
                    _frozenGfxTimer--;
                    if (HudViewer && _frozenGfxTimer == 0)
                    {
                        _drawIceLayer = false;
                    }
                }
                if (_timeSinceFrozen != UInt16.MaxValue)
                {
                    _timeSinceFrozen++;
                }
            }
            else
            {
                _showScoreboard = GameState.Multiplayer && Controls.Pause.IsDown;
                _svPlaced = false; // placed on the stage again after the respawn
            }
            SideViewBiped();
        }

        // puts the player on a stage spawn, standing, facing the middle
        private void SideViewPlace(SideViewStage stage)
        {
            Vector2 spawn = stage.Spawns.Length > 0 ? stage.Spawns[SlotIndex % stage.Spawns.Length] : Vector2.Zero;
            Position = new Vector3(0, spawn.Y - Fixed.ToFloat(Values.MinPickupHeight), spawn.X);
            PrevPosition = Position;
            Speed = Vector3.Zero;
            // the room part at the new spot: the per-frame walk (UpdateNodeRef) crosses one portal at most, so after a
            // jump across the room the old part stays and the player isn't drawn (IsVisible(NodeRef) in PlayerDraw)
            NodeRef part = _scene.GetNodeRefContaining(Position + PlayerVolumes[(int)Hunter, 0].SpherePosition);
            if (part.PartIndex != -1)
            {
                NodeRef = part;
                CameraInfo.NodeRef = part;
            }
            _svFacing = spawn.X > 0 ? -1 : 1;
            _svElev = 0;
            _svAim = new Vector2(_svFacing, 0);
            _svCursor = _svAim * SideViewCursorRadius;
            _svCursorFree = false;
            _svAimHold = 0;
            _svDropTimer = 0;
            _svOnSoft = false;
            _svStance = false;
            _svLaserHit = false;
            SideViewAimVecs();
            _volume = CollisionVolume.Move(_volumeUnxf, Position);
            _svPlaced = true;
        }

        // the gun points along the facing, raised by the elevation (held short of straight up or down, where the facing
        // would have no run direction); the view faces the same way (UpdateAimVecs pulls the aim toward the facing). The
        // shot itself goes along the true aim (SideViewShotVecs)
        private void SideViewAimVecs()
        {
            float e = MathHelper.DegreesToRadians(Math.Clamp(_svElev, -85, 85));
            _gunVec1 = new Vector3(0, MathF.Sin(e), _svFacing * MathF.Cos(e));
            _facingVector = _gunVec1;
            _aimY = Math.Clamp(_svElev, -85, 85);
        }

        // the aim, all the way round on the plane (owner 2026-11-12: "aim in a full circle around you ... like you can in
        // dread"): one unit vector (z, y). The right stick points it; in the stance the move stick (or keys) points it too;
        // the mouse moves a reticle freely over the screen (owner 11-12: "the reticle to follow the mouse freely"; it stays
        // where the mouse put it as the player and the camera move, kept inside the camera's frame) and the aim points from
        // the muzzle at it; the keyboard's aim keys raise, lower and turn it. The facing turns once the aim is past straight up or down
        // (SideViewFlipSin), so the aim goes over the top without a jump. With no aim input for half a second, running
        // levels it toward the run (holding fire keeps it)
        private void SideViewAim(float run, float moveX, float moveY)
        {
            bool aimed = false;
            bool main = !IsBot && this == Main;
            float sx = main ? SideViewStickX : 0;
            float sy = main ? SideViewStickY : 0;
            float stick = MathF.Sqrt(sx * sx + sy * sy);
            if (stick > SideViewStickAim)
            {
                SideViewPoint(new Vector2(sx, -sy));
                aimed = true;
            }
            else if (_svStance && MathF.Sqrt(moveX * moveX + moveY * moveY) > SideViewStickAim)
            {
                SideViewPoint(new Vector2(moveX, moveY));
                aimed = true;
            }
            else
            {
                // (the host adds the right stick to the mouse's deltas: only while it's at rest)
                if (Controls.MouseAim && !Flags1.TestFlag(PlayerFlags1.NoAimInput) && main && stick <= SideViewStickRest)
                {
                    float dz = Input.MouseDeltaX;
                    float dy = -Input.MouseDeltaY;
                    if (CameraSequence.Current?.Flags.TestFlag(CamSeqFlags.BlockInput) == true
                        || _scene.FrameAdvance || _scene.FrameAdvanceLastFrame)
                    {
                        dz = dy = 0;
                    }
                    if (MathF.Abs(dz) > IdleAimDeadZone || MathF.Abs(dy) > IdleAimDeadZone)
                    {
                        Input.HasInput = true;
                    }
                    if (Controls.InvertAimY)
                    {
                        dy *= -1;
                    }
                    if (Controls.InvertAimX)
                    {
                        dz *= -1;
                    }
                    if (dz != 0 || dy != 0)
                    {
                        if (!_svCursorFree)
                        {
                            // from where the reticle is drawn now
                            _svCursorOff = new Vector2(_svMuzzle.Z, _svMuzzle.Y) + _svCursor - SideViewFrameCentre;
                            _svCursorFree = true;
                        }
                        _svCursorOff = SideViewClampToFrame(_svCursorOff + new Vector2(dz, dy) * SideViewMouseScale);
                    }
                    if (_svCursorFree)
                    {
                        // the aim follows the reticle every frame, the mouse still or not
                        _svCursor = SideViewFrameCentre + SideViewClampToFrame(_svCursorOff) - new Vector2(_svMuzzle.Z, _svMuzzle.Y);
                        if (_svCursor.Length > SideViewCursorMin)
                        {
                            _svAim = _svCursor.Normalized();
                            SideViewFaceAim();
                        }
                        aimed = true;
                    }
                }
                if (Controls.KeyboardAim && main)
                {
                    bool keyed = false;
                    float elev = MathHelper.RadiansToDegrees(MathF.Atan2(_svAim.Y, _svFacing * _svAim.X));
                    float face = _svFacing;
                    if (Controls.AimUp.IsDown)
                    {
                        elev = MathF.Min(elev + 3, 90);
                        keyed = true;
                    }
                    else if (Controls.AimDown.IsDown)
                    {
                        elev = MathF.Max(elev - 3, -90);
                        keyed = true;
                    }
                    if (Controls.AimRight.IsDown)
                    {
                        face = 1;
                        keyed = true;
                    }
                    else if (Controls.AimLeft.IsDown)
                    {
                        face = -1;
                        keyed = true;
                    }
                    if (keyed)
                    {
                        elev = Math.Clamp(elev, -90, 90);
                        _svFacing = face;
                        float e = MathHelper.DegreesToRadians(elev);
                        _svAim = new Vector2(face * MathF.Cos(e), MathF.Sin(e));
                        _svCursor = _svAim * SideViewCursorRadius;
                        _svCursorFree = false;
                        aimed = true;
                    }
                }
            }
            if (aimed)
            {
                _svAimHold = 0;
            }
            else if (_svAimHold < Int32.MaxValue)
            {
                _svAimHold++;
            }
            if (run != 0 && _svAimHold > 30 && !Controls.Shoot.IsDown)
            {
                _svFacing = MathF.Sign(run);
                _svAim = new Vector2(_svFacing, 0);
                _svCursor = _svAim * SideViewCursorRadius;
                _svCursorFree = false;
            }
            _svElev = MathHelper.RadiansToDegrees(MathF.Atan2(_svAim.Y, _svFacing * _svAim.X));
            SideViewAimVecs();
        }

        // a stick's direction: the aim, and the reticle at its full distance
        private void SideViewPoint(Vector2 dir)
        {
            _svAim = dir.Normalized();
            _svCursor = _svAim * SideViewCursorRadius;
            _svCursorFree = false;
            SideViewFaceAim();
        }

        // the free reticle's offset from the frame's centre, kept a little inside the camera's frame (none before the
        // host has set one)
        private static Vector2 SideViewClampToFrame(Vector2 off)
        {
            Vector2 half = SideViewFrameHalf - new Vector2(0.5f, 0.5f);
            if (half.X <= 0 || half.Y <= 0)
            {
                return off;
            }
            return new Vector2(Math.Clamp(off.X, -half.X, half.X), Math.Clamp(off.Y, -half.Y, half.Y));
        }

        // the facing turns once the aim is SideViewFlipSin past straight up or down to the other side
        private void SideViewFaceAim()
        {
            if (_svAim.X * _svFacing < -SideViewFlipSin)
            {
                _svFacing = -_svFacing;
            }
        }

        // after the input step's UpdateAimVecs: the shot leaves on the plane along the true aim (UpdateAimVecs pulls it
        // toward the facing, which stops short of straight up or down); in the stance, the laser's end
        private void SideViewShotVecs()
        {
            var dir = new Vector3(0, _svAim.Y, _svAim.X);
            _muzzlePos = _gunDrawPos + dir * Fixed.ToFloat(Values.MuzzleOffset);
            _muzzlePos.X = 0;
            _aimVec = dir;
            _aimPosition = _muzzlePos + dir * Fixed.ToFloat(Values.AimDistance);
            _svMuzzle = _muzzlePos;
            _svLaserHit = false;
            if (!_svStance)
            {
                _svLaserEnd = _muzzlePos;
                return;
            }
            // the room as the beams meet it, then the nearest other player's body box on the plane
            Vector3 end = _muzzlePos + dir * SideViewLaserReach;
            var result = new CollisionResult();
            if (CollisionDetection.CheckBetweenPoints(_muzzlePos, end, TestFlags.Beams, _scene, ref result))
            {
                end = result.Position;
                end.X = 0;
            }
            float reach = (end - _muzzlePos).Length;
            for (int i = 0; i < Players.Count; i++)
            {
                PlayerEntity q = Players[i];
                if (q == this || !q.LoadFlags.TestFlag(LoadFlags.Active) || !q.LoadFlags.TestFlag(LoadFlags.Spawned)
                    || q._health == 0)
                {
                    continue;
                }
                float r = Fixed.ToFloat(q.Values.BipedColRadius);
                float t = SideViewRayBox(_muzzlePos.Z, _muzzlePos.Y, dir.Z, dir.Y, q.Position.Z - r, q.Position.Z + r,
                    q.Position.Y + Fixed.ToFloat(q.Values.MinPickupHeight), q.Position.Y + Fixed.ToFloat(q.Values.MaxPickupHeight));
                if (t >= 0 && t < reach)
                {
                    reach = t;
                    _svLaserHit = true;
                }
            }
            _svLaserEnd = _muzzlePos + dir * reach;
        }

        // where the ray (z, y) + t (dz, dy) enters the box, t >= 0 (0 when it starts inside), or -1 if it misses
        public static float SideViewRayBox(float z, float y, float dz, float dy, float zMin, float zMax, float yMin, float yMax)
        {
            float tMin = 0;
            float tMax = Single.MaxValue;
            if (!SideViewSlab(z, dz, zMin, zMax, ref tMin, ref tMax) || !SideViewSlab(y, dy, yMin, yMax, ref tMin, ref tMax))
            {
                return -1;
            }
            return tMin;
        }

        private static bool SideViewSlab(float at, float d, float min, float max, ref float tMin, ref float tMax)
        {
            if (MathF.Abs(d) < 1e-6f)
            {
                return at >= min && at <= max;
            }
            float t1 = (min - at) / d;
            float t2 = (max - at) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            return tMin <= tMax;
        }

        // copy of ProcessBiped (PlayerInput.cs) for the side view
        private void SideViewBiped()
        {
            SideViewStage? stage = SideStage;
            if (stage == null)
            {
                return;
            }
            if (!_svPlaced && _health > 0)
            {
                SideViewPlace(stage);
            }
            if (IsMainPlayer && GameState.SinglePlayer && CameraSequence.Current != null)
            {
                _timeIdle = 0;
            }
            if (EquipInfo.SmokeLevel < EquipInfo.Weapon.SmokeDrain)
            {
                EquipInfo.SmokeLevel = 0;
            }
            else
            {
                EquipInfo.SmokeLevel -= EquipInfo.Weapon.SmokeDrain;
            }
            Vector3 speedDelta = Vector3.Zero;
            PlayerAnimation anim1 = PlayerAnimation.None;
            PlayerAnimation anim2 = PlayerAnimation.None;
            AnimFlags animFlags1 = AnimFlags.None;
            AnimFlags animFlags2 = AnimFlags.None;
            float run = 0;
            _svStance = false;
            if (_frozenTimer == 0 && _health > 0 && !_field6D0)
            {
                // the stance (held): planted, the move stick aims
                _svStance = !IsBot && this == Main && (SideViewStanceHeld || Controls.Zoom.IsDown);
                run = _svStance ? 0 : MoveRightAxis;
                SideViewAim(run, MoveRightAxis, MoveForwardAxis);
                bool jumping = false;
                if (!Flags2.TestAny(PlayerFlags2.BipedLock | PlayerFlags2.BipedStuck) && !ShieldWallLocked && !WallClingHeld && !ParalyzerStunned)
                {
                    if (run != 0)
                    {
                        // MoveForwardBack's walk, along z
                        Flags1 |= PlayerFlags1.MovingBiped;
                        if (Flags1.TestFlag(PlayerFlags1.Standing))
                        {
                            Flags1 |= PlayerFlags1.Walking;
                        }
                        else
                        {
                            Flags1 &= ~PlayerFlags1.Walking;
                        }
                        float traction = Fixed.ToFloat(Values.WalkBipedTraction);
                        if (_jumpPadControlLockMin > 0)
                        {
                            traction *= Fixed.ToFloat(Values.JumpPadSlideFactor);
                        }
                        else if (Flags1.TestFlag(PlayerFlags1.Standing) && _slipperiness != 0)
                        {
                            traction *= Metadata.TractionFactors[_slipperiness];
                        }
                        if (_jumpPadControlLockMin <= 0)
                        {
                            traction *= MovePushScale * SideViewRunScale;
                        }
                        speedDelta.Z += traction * MathF.Sign(run);
                        if (Flags1.TestFlag(PlayerFlags1.Grounded) && _timeSinceJumpPad > 7 * 2)
                        {
                            anim1 = MathF.Sign(run) == _svFacing ? PlayerAnimation.WalkForward : PlayerAnimation.WalkBackward;
                        }
                    }
                    _field684 = 0;
                    _field688 = 0;
                    if (Cheats.UnlimitedJumps)
                    {
                        Flags1 &= ~PlayerFlags1.UsedJump;
                    }
                    // Down on a soft floor drops through it
                    if (!_svStance && MoveForwardAxis < -0.5f && _svOnSoft && Flags1.TestFlag(PlayerFlags1.Standing) && _svDropTimer == 0)
                    {
                        _svDropTimer = SideViewDropFrames;
                    }
                    if (_jumpPadControlLockMin == 0 && (Controls.Jump.IsPressed || TouchJumpPressed)
                        && !Flags1.TestFlag(PlayerFlags1.UsedJump))
                    {
                        jumping = true;
                        if (!Flags1.TestFlag(PlayerFlags1.Standing) || !_abilities.TestFlag(AbilityFlags.SpaceJump))
                        {
                            Flags1 |= PlayerFlags1.UsedJump;
                        }
                        Speed = Speed.WithY(SideViewJumpV0());
                        _gravityHold = 2;
                        _timeSinceGrounded = 8 * 2;
                        PlayHunterSfx(HunterSfx.Jump);
                    }
                }
                if (jumping || _timeSinceJumpPad == 1)
                {
                    animFlags1 = AnimFlags.NoLoop;
                    if (run != 0 && MathF.Sign(run) == _svFacing)
                    {
                        anim1 = PlayerAnimation.JumpForward;
                    }
                    else if (run != 0)
                    {
                        anim1 = PlayerAnimation.JumpBack;
                    }
                    else
                    {
                        anim1 = PlayerAnimation.JumpNeutral;
                    }
                }
            }
            SideViewMovement(stage, speedDelta.Z != 0);
            UpdateCamera();
            UpdateAimVecs();
            // the shot leaves on the plane, along the true aim
            SideViewShotVecs();
            if (_frozenTimer == 0 && _health > 0 && !_field6D0)
            {
                if (!IsUnmorphing)
                {
                    if (!Controls.Shoot.IsDown)
                    {
                        Flags2 &= ~PlayerFlags2.Shooting;
                    }
                    else if (Controls.Shoot.IsPressed || !Flags2.TestFlag(PlayerFlags2.NoShotsFired))
                    {
                        Flags2 |= PlayerFlags2.Shooting;
                        Flags2 &= ~PlayerFlags2.NoShotsFired;
                    }
                    if (!_availableCharges[CurrentWeapon] || !EquipWeapon.Flags.TestFlag(WeaponFlags.CanCharge))
                    {
                        EquipInfo.ChargeLevel = 0;
                    }
                    else
                    {
                        bool releaseCharge = false;
                        if (!Flags2.TestFlag(PlayerFlags2.Shooting) || EquipInfo.Ammo < EquipWeapon.ChargeCost)
                        {
                            releaseCharge = true;
                        }
                        else
                        {
                            if (EquipInfo.ChargeLevel > 0 && GunAnimation != GunAnimation.MissileClose)
                            {
                                if ((CurrentWeapon != BeamType.PowerBeam
                                    || EquipInfo.ChargeLevel >= EquipInfo.Weapon.MinCharge * 2)
                                    && !HyperBeamRapidCharge)
                                {
                                    PlayBeamChargeSfx(CurrentWeapon);
                                }
                                if (Biped2Flags.TestFlag(AnimFlags.Ended) || Biped2Anim == PlayerAnimation.Charge
                                    || Biped2Anim == PlayerAnimation.Shoot && Biped2Frame > 8)
                                {
                                    anim2 = PlayerAnimation.Charge;
                                }
                            }
                            if (EquipInfo.ChargeLevel >= EquipWeapon.FullCharge * 2)
                            {
                                EquipInfo.SmokeLevel += EquipWeapon.SmokeChargeAmount;
                                EquipInfo.SmokeLevel = (ushort)Math.Min(EquipInfo.SmokeLevel, EquipWeapon.SmokeStart * 2);
                            }
                            else
                            {
                                EquipInfo.ChargeLevel++;
                                int minCharge = EquipWeapon.MinCharge * 2;
                                if (EquipInfo.ChargeLevel > minCharge)
                                {
                                    int fullCharge = EquipWeapon.FullCharge * 2;
                                    int chargeCost = EquipWeapon.ChargeCost * 2;
                                    int minCost = EquipWeapon.MinChargeCost * 2;
                                    int cost = minCost + (chargeCost - minCost) * (EquipInfo.ChargeLevel - minCharge) / (fullCharge - minCharge);
                                    if (EquipInfo.Ammo < cost / 2)
                                    {
                                        EquipInfo.ChargeLevel--;
                                    }
                                }
                            }
                        }
                        if (releaseCharge)
                        {
                            StopBeamChargeSfx(CurrentWeapon);
                            if (EquipInfo.ChargeLevel >= EquipWeapon.MinCharge * 2
                                && !HyperBeamRapidCharge)
                            {
                                TryFireWeapon();
                                anim2 = PlayerAnimation.ChargeShoot;
                                animFlags2 = AnimFlags.NoLoop;
                            }
                            EquipInfo.ChargeLevel = 0;
                        }
                    }
                    bool paralyzerPending = ParalyzerPendingShot();
                    if ((Controls.Shoot.IsPressed && EquipInfo.ChargeLevel <= 1 * 2
                        || EquipWeapon.Flags.TestFlag(WeaponFlags.RepeatFire) && Flags2.TestFlag(PlayerFlags2.Shooting)
                        && (!EquipWeapon.Flags.TestFlag(WeaponFlags.CanCharge) || EquipInfo.ChargeLevel < EquipWeapon.MinCharge * 2)
                        || paralyzerPending)
                        && !BotSightHold())
                    {
                        if (TryFireWeapon())
                        {
                            _paralyzerPressPending = false;
                            anim2 = PlayerAnimation.Shoot;
                            animFlags2 |= AnimFlags.NoLoop;
                            if (Biped2Anim == PlayerAnimation.Shoot)
                            {
                                SetBiped2Animation(PlayerAnimation.Shoot, Biped2Flags);
                            }
                        }
                    }
                }
                speedDelta = KnockSteerCut(speedDelta);
                float magBefore = MathF.Abs(Speed.Z);
                Speed += speedDelta;
                Speed = Speed.WithX(0);
                float magAfter = MathF.Abs(Speed.Z);
                float walkCap = _hSpeedCap;
                if (_jumpPadControlLockMin <= 0 && !Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
                {
                    walkCap *= MovePushScale;
                }
                if (magAfter > magBefore && magAfter > walkCap)
                {
                    float factor;
                    if (magBefore <= walkCap)
                    {
                        factor = walkCap / magAfter;
                    }
                    else
                    {
                        factor = magBefore / magAfter;
                    }
                    Speed = Speed.WithZ(Speed.Z * factor);
                }
                if (anim1 == PlayerAnimation.None)
                {
                    if (Flags1.TestFlag(PlayerFlags1.Grounded))
                    {
                        if (Biped1Anim == PlayerAnimation.Idle)
                        {
                            if (++_timeIdle > 300 * 2 && _timeSinceInput > 300 * 2)
                            {
                                SetBiped1Animation(PlayerAnimation.Flourish, AnimFlags.NoLoop);
                            }
                        }
                        else if (!Biped1Flags.TestFlag(AnimFlags.NoLoop) || Biped1Flags.TestFlag(AnimFlags.Ended))
                        {
                            _timeIdle = 0;
                            SetBiped1Animation(PlayerAnimation.Idle, AnimFlags.None);
                        }
                    }
                }
                else if (anim1 != Biped1Anim || anim1 == PlayerAnimation.JumpForward || anim1 == PlayerAnimation.JumpBack
                    || anim1 == PlayerAnimation.JumpLeft || anim1 == PlayerAnimation.JumpRight || anim1 == PlayerAnimation.JumpNeutral)
                {
                    SetBiped1Animation(anim1, animFlags1);
                }
                if (anim2 == PlayerAnimation.None)
                {
                    if ((!Biped2Flags.TestFlag(AnimFlags.NoLoop) || Biped2Flags.TestFlag(AnimFlags.Ended)) && Biped2Anim != Biped1Anim)
                    {
                        SetBiped2Animation(Biped1Anim, Biped1Flags);
                        _bipedModel2.AnimInfo.Frame[0] = Biped1Frame;
                    }
                }
                else if (anim2 != Biped2Anim)
                {
                    SetBiped2Animation(anim2, animFlags2);
                }
            }
            if (_health > 0 && stage.PastKo(Position))
            {
                SideViewKOs++;
                SideViewPlace(stage);
            }
        }

        // copy of ProcessMovement (PlayerInput.cs) for the side view; the stage in place of CheckCollision
        private void SideViewMovement(SideViewStage stage, bool running)
        {
            if (_accelerationTimer > 0)
            {
                _accelerationTimer--;
                Speed += Acceleration / 2;
            }
            float hSpeedMag = MathF.Abs(Speed.Z);
            if (hSpeedMag == 0)
            {
                _hSpeedMag = 0;
            }
            else
            {
                _hSpeedCap = Fixed.ToFloat(Values.WalkSpeedCap);
                if (IsPrimeHunter)
                {
                    _hSpeedCap = 0.4f;
                }
                _hSpeedCap *= SideViewRunScale;
                _hSpeedMag = hSpeedMag;
            }
            float hMag = MathF.Sqrt(_facingVector.X * _facingVector.X + _facingVector.Z * _facingVector.Z);
            _field70 = _facingVector.X / hMag;
            _field74 = _facingVector.Z / hMag;
            _gunVec2 = new Vector3(_field74, 0, -_field70);
            _field78 = _gunVec2.X;
            _field7C = _gunVec2.Z;
            _upVector = Vector3.Cross(_facingVector, _gunVec2).Normalized();
            _aimPosition = _gunVec1 * Fixed.ToFloat(Values.AimDistance);
            _aimPosition += CameraInfo.Position;
            if (Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
            {
                float prevZ = Speed.Z;
                Speed = Speed.AddZ(-_jumpPadAccel.Z);
                if (!_jumpPadKeepPush && (prevZ <= 0 && Speed.Z > 0 || prevZ > 0 && Speed.Z < 0))
                {
                    _jumpPadAccel.Z += Speed.Z / 2;
                    Speed = Speed.WithZ(0);
                }
            }
            float slideSfxAmount = 0;
            float speedFactor;
            if (Flags1.TestFlag(PlayerFlags1.Standing))
            {
                if (Flags1.TestFlag(PlayerFlags1.Walking))
                {
                    speedFactor = Fixed.ToFloat(Values.WalkSpeedFactor);
                }
                else
                {
                    speedFactor = Fixed.ToFloat(Values.StandSpeedFactor);
                }
            }
            else
            {
                speedFactor = Fixed.ToFloat(Values.AirSpeedFactor);
            }
            if (Flags1.TestFlag(PlayerFlags1.Standing) && _slipperiness != 0)
            {
                speedFactor += (1 - speedFactor) * Metadata.SlipSpeedFactors[_slipperiness];
                if (!Flags1.TestFlag(PlayerFlags1.MovingBiped))
                {
                    slideSfxAmount = 0xFFFF * _hSpeedMag / Fixed.ToFloat(Values.WalkSpeedCap);
                }
            }
            UpdateSlidingSfx(slideSfxAmount);
            if (Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
            {
                Vector3 speedMul = Speed.WithZ(Speed.Z * speedFactor);
                Speed += (speedMul - Speed) / 2;
                _walkFriction = 1;
            }
            else
            {
                _walkFriction = MathF.Sqrt(speedFactor);
                Speed = Speed.WithZ(Speed.Z * _walkFriction);
            }
            if (Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
            {
                Speed = Speed.AddZ(_jumpPadAccel.Z);
            }
            Speed = Speed.WithX(0);
            if (Flags1.TestFlag(PlayerFlags1.Standing) && _timeSinceJumpPad > 5 * 2)
            {
                _lastJumpPad = null;
                Flags1 &= ~PlayerFlags1.UsedJumpPad;
                _jumpPadControlLock = 0;
                _jumpPadControlLockMin = 0;
            }
            if (Speed.Y <= 0.01f)
            {
                Flags2 &= ~PlayerFlags2.AltFormGravity;
            }
            bool holdGravity = _gravityHold == 1;
            if (_gravityHold > 0)
            {
                _gravityHold--;
            }
            float gravityStep = 0;
            Vector3 prev = Position;
            if (_health > 0)
            {
                if (_jumpPadControlLock == 0 && !Flags2.TestFlag(PlayerFlags2.BipedStuck))
                {
                    if (holdGravity)
                    {
                        // keep the jump frame's _gravity
                    }
                    else if (Flags2.TestFlag(PlayerFlags2.GravityOverride))
                    {
                        Flags2 &= ~PlayerFlags2.GravityOverride;
                    }
                    else if (Flags1.TestFlag(PlayerFlags1.Standing) && _slipperiness == 0)
                    {
                        _gravity = 0;
                    }
                    else
                    {
                        _gravity = Fixed.ToFloat(Values.BipedGravity);
                    }
                    gravityStep = _gravity / 2;
                    Speed = Speed.AddY(gravityStep);
                }
                Vector3 position = Position + Speed / 2;
                position.Y += gravityStep / 4;
                position.Z += Speed.Z * (_walkFriction - 1) / 2;
                position.X = 0;
                Position = position;
                CheckPlayerCollision();
                Position = Position.WithX(0);
            }
            _standingEntCol = null;
            _collidedEntCol = null;
            _terrainDamage = false;
            if (!Flags1.TestFlag(PlayerFlags1.CollidingLateral))
            {
                _horizColTimer = 0;
            }
            else if (_horizColTimer != UInt16.MaxValue)
            {
                _horizColTimer++;
            }
            bool standingPrev = Flags1.TestFlag(PlayerFlags1.Standing);
            bool noUnmorphPev = Flags1.TestFlag(PlayerFlags1.NoUnmorph);
            Flags1 &= ~PlayerFlags1.Standing;
            Flags1 &= ~PlayerFlags1.StandingPrevious;
            Flags1 &= ~PlayerFlags1.NoUnmorph;
            Flags1 &= ~PlayerFlags1.NoUnmorphPrevious;
            Flags1 &= ~PlayerFlags1.OnLava;
            Flags1 &= ~PlayerFlags1.OnAcid;
            Flags2 &= ~PlayerFlags2.SpireClimbing;
            Flags1 &= ~PlayerFlags1.CollidingLateral;
            Flags1 &= ~PlayerFlags1.CollidingEntity;
            if (standingPrev)
            {
                Flags1 |= PlayerFlags1.StandingPrevious;
            }
            if (noUnmorphPev)
            {
                Flags1 |= PlayerFlags1.NoUnmorphPrevious;
            }
            // the stage in place of CheckCollision
            if (_svDropTimer > 0)
            {
                _svDropTimer--;
            }
            Vector3 pos = Position;
            Vector3 speed = Speed;
            SideViewStage.Contact contact = stage.Resolve(prev, ref pos, ref speed, Fixed.ToFloat(Values.MinPickupHeight),
                Fixed.ToFloat(Values.MaxPickupHeight), Fixed.ToFloat(Values.BipedColRadius), dropping: _svDropTimer > 0);
            Position = pos;
            Speed = speed;
            _svOnSoft = contact.Standing && contact.Soft;
            if (contact.Standing)
            {
                Flags1 |= PlayerFlags1.Standing;
                _timeSinceStanding = 0;
                if (!Flags1.TestFlag(PlayerFlags1.StandingPrevious) || Flags1.TestFlag(PlayerFlags1.AltFormPrevious))
                {
                    Flags1 &= ~PlayerFlags1.UsedJump;
                }
                if (_health > 0 && !Flags1.TestFlag(PlayerFlags1.Grounded))
                {
                    if (Biped1Anim == PlayerAnimation.JumpLeft)
                    {
                        SetBiped1Animation(PlayerAnimation.LandLeft, AnimFlags.NoLoop);
                    }
                    else if (Biped1Anim == PlayerAnimation.JumpRight)
                    {
                        SetBiped1Animation(PlayerAnimation.LandRight, AnimFlags.NoLoop);
                    }
                    else
                    {
                        SetBiped1Animation(PlayerAnimation.LandNeutral, AnimFlags.NoLoop);
                    }
                }
                _slipperiness = 0;
            }
            if (contact.Lateral)
            {
                Flags1 |= PlayerFlags1.CollidingLateral;
            }
            _volume = CollisionVolume.Move(_volumeUnxf, Position);
            _fieldC0 = Vector3.UnitY;
            if (Flags1.TestFlag(PlayerFlags1.Standing) && !Flags1.TestFlag(PlayerFlags1.StandingPrevious))
            {
                // landing
                _timeStanding = 0;
                if (PrevSpeed.Y >= 0)
                {
                    _field44C = 0;
                }
                else
                {
                    _field44C = -PrevSpeed.Y * 0.35f;
                    if (_field44C > Fixed.ToFloat(800))
                    {
                        _field44C = Fixed.ToFloat(800);
                    }
                    if (PrevSpeed.Y < -0.65f)
                    {
                        CameraInfo.SetShake(Fixed.ToFloat(204));
                    }
                }
            }
            else if (_timeStanding != UInt16.MaxValue)
            {
                _timeStanding++;
            }
            if (Flags1.TestFlag(PlayerFlags1.Grounded))
            {
                Flags1 |= PlayerFlags1.GroundedPrevious;
            }
            else
            {
                Flags1 &= ~PlayerFlags1.GroundedPrevious;
            }
            if (Flags1.TestFlag(PlayerFlags1.Standing))
            {
                _timeBeforeLanding = _timeSinceGrounded;
                _timeSinceGrounded = 0;
                Flags1 |= PlayerFlags1.Grounded;
            }
            else if (_timeSinceGrounded < 90 * 2)
            {
                _timeSinceGrounded++;
                if (_timeSinceGrounded >= 8 * 2)
                {
                    Flags1 &= ~PlayerFlags1.Grounded;
                    _walkSfxTimer = 0;
                    _walkSfxIndex = 0;
                }
            }
            bool burning = _health > 0 && _burnTimer > 0;
            UpdateBurningSfx(burning);
            if (Flags1.TestFlag(PlayerFlags1.Grounded))
            {
                UpdateWalkingSfx();
            }
        }
    }
}
