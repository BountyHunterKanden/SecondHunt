using System;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the Trooper's Wall Cling (docs/CHARACTERS_AND_SKINS.md, D17). Host-guarded: off unless the match host turns
    // it on for a seat (HostWallCling, a Trooper seat in a local match) before the match loads; with it off nothing here
    // runs and the player is MphRead's own. Owner, 2026-10-08: "You can jump at a wall to cling to it for a moment and
    // shoot from the wall before you need to jump again or fall a few seconds later".
    //   - starts when he's in the air (not standing or just off the ground, not on a jump pad's flight, a biped) and
    //     moves into a wall: a collision FACE (not an edge or corner) standing straight up or leaning back a little --
    //     at most 1 degree overhanging, at most 5 degrees leaning back (owner 2026-10-08: "Only straight and slightly
    //     obtuse angles with the ground", no acute ones); MphRead's own walls go to 5.7 degrees either way
    //   - while clinging: no falling and no walking; aiming, firing and weapon switches are free
    //   - Jump = wall jump: up at jump speed and pushed off the wall; Morph drops off (he morphs); after WallClingTime
    //     he lets go and falls
    //   - the wall must be at least as tall as he is where he hits it (owner 2026-10-08: no clinging to "really small
    //     short walls"): rays straight into it from his feet, waist and head must all meet the same straight wall face
    //   - another cling needs a different wall (or landing first) and a short wait, so he can't climb one wall
    //   - at most WallClingChain clings before he touches the ground again (owner 2026-10-08: "One initial jump and
    //     cling, and two more clings can be chained but not more until the trooper touches the ground"); with
    //     HostWallClingOnce one (2026-10-19: one cling, no chained wall hops)
    //   - bots don't cling (yet)
    // Speeds and times here are sim code (60 Hz frames); only sqrt, no trig (replays and LAN stay bit-exact).
    public partial class PlayerEntity
    {
        // set by the match host for this seat before the match loads (default off: MphRead's own player)
        public bool HostWallCling { get; set; }
        // N10 (release test notes, owner 2026-10-10: he "walks" into a cling where the floor/wall geometry is busy): a cling
        // needs a jump since he last stood, or a real fall (WallClingMinDrop). -wallclingprobe, all MP arenas, wandering
        // without a jump: every accidental cling came after 16-214 frames off the ground but dropping only 0-0.8 units (he
        // slides over busy floors without counting as standing), never after a jump. Off: replays from before it.
        public bool HostWallClingJumpRule { get; set; } = true;
        public const float WallClingMinDrop = 2.0f; // units below the highest point since he stood (taller than he is)
        // 2026-10-19 (MatchSettings.WallClingOnce, set per seat by MatchStart; older replays: off): one cling per airtime,
        // no chained wall hops; off: WallClingChain (3)
        public bool HostWallClingOnce { get; set; }
        public int WallClingChainLimit => HostWallClingOnce ? 1 : WallClingChain;
        // 2026-10-20 (clinging by accident was too easy; MatchSettings.WallClingHold, set per seat by MatchStart; older
        // replays: off): a cling starts only while Jump is held, and not before WallClingHoldDelay frames since his last jump
        // (or wall jump) or since he left the ground, so a jump press held a moment too long doesn't stick him to a wall he
        // brushes on the way up. Held against the wall after the wait, he clings as soon as it's up
        public bool HostWallClingHold { get; set; }
        public const int WallClingHoldDelay = 18; // frames (60 Hz): 0.3 s

        public const int WallClingTime = 150; // frames (60 Hz): 2.5 s
        public const int WallClingChain = 3; // clings between touching the ground
        public const float WallClingMinNormalY = -0.0175f; // sin(1 degree): the most a wall may overhang
        public const float WallClingMaxNormalY = 0.0872f; // sin(5 degrees): the most a wall may lean back
        private const int _clingWait = 20; // after letting go, frames before another cling
        private const float _clingMinInto = 0.02f; // horizontal speed into the wall that counts as jumping at it
        private const float _clingSameWall = 0.7f; // cos between wall normals above which it's the wall he last left
        private const float _clingJumpPush = 0.3f; // a wall jump's horizontal speed off the wall
        private const int _clingKickTime = 14;
        private const float _clingRiseSpeed = 0.1f; // upward speed that counts as having jumped (a jump starts at ~0.25-0.35) // after a wall jump, frames in which steering back into that wall is ignored

        private bool _clinging = false;
        private int _clingTimer = 0;
        private int _clingWaitTimer = 0;
        private int _clingKickTimer = 0;
        private Vector3 _clingNormal = Vector3.Zero; // horizontal unit normal of the wall he's on
        private Vector3 _clingLastNormal = Vector3.Zero; // the last wall he clung to since standing (zero: none)
        private int _clingChainCount = 0; // clings since he last stood
        private const float _clingProbeReach = 0.35f; // past his collision radius, how far the height rays look
        private const float _clingProbeInset = 0.12f; // the foot and head rays, this far in from his feet / head
        private float _clingTouchNormalY = 0;
        // this frame's wall contact (from HandleCollision): the face moved into the most
        private bool _clingTouched = false;
        private Vector3 _clingTouchNormal = Vector3.Zero;
        private float _clingTouchInto = 0;
        // since he last stood: frames in the air, whether he jumped (a jump or wall jump: not a walk off a ledge), and
        // the highest he's been
        private int _clingAirFrames = 0;
        private bool _clingRose = false;
        private float _clingTopY = 0;
        private int _clingSinceJump = 0; // frames since his last jump or wall jump, or since he stood

        public bool WallClinging => _clinging;
        public int WallClingTimer => _clingTimer;
        public Vector3 WallClingNormal => _clingNormal;
        public float WallClingNormalY { get; private set; } // the wall face's own normal.Y (tests)
        public int WallClingChainCount => _clingChainCount;
        public int WallClingTooShort { get; private set; } // clings refused: the wall shorter than he is (tests)
        public int WallClingAirFrames => _clingAirFrames; // (tests, -wallclingprobe)
        public bool WallClingRose => _clingRose;
        public int WallClingStartAir { get; private set; } // the last cling's air frames, jump, drop when it started (tests)
        public bool WallClingStartRose { get; private set; }
        public float WallClingStartDrop { get; private set; }
        // counts for tests (-wallclingtest)
        public int WallClings { get; private set; }
        public int WallJumps { get; private set; }
        public int WallClingTimeouts { get; private set; }
        public int WallClingDrops { get; private set; }
        public int WallClingLongest { get; private set; }

        private bool WallClingHeld => HostWallCling && _clinging;

        // Initialize (spawn and respawn)
        private void WallClingInit()
        {
            _clinging = false;
            _clingTimer = 0;
            _clingWaitTimer = 0;
            _clingKickTimer = 0;
            _clingNormal = Vector3.Zero;
            _clingLastNormal = Vector3.Zero;
            _clingChainCount = 0;
            _clingTouched = false;
            _clingAirFrames = 0;
            _clingRose = false;
            _clingTopY = Position.Y;
            _clingSinceJump = 0;
        }

        // HandleCollision, on a wall contact (|normal.Y| < 0.1), before the push-out changes Speed. face: a collision face's
        // own plane (false: an edge or corner, whose normal MphRead makes from the contact point -- never clung to)
        private void WallClingTouch(Vector3 normal, bool face)
        {
            if (!face || normal.Y < WallClingMinNormalY || normal.Y > WallClingMaxNormalY)
            {
                return;
            }
            float len = MathF.Sqrt(normal.X * normal.X + normal.Z * normal.Z);
            if (len < 1e-4f)
            {
                return;
            }
            var n = new Vector3(normal.X / len, 0, normal.Z / len);
            float into = -(Speed.X * n.X + Speed.Z * n.Z);
            if (!_clingTouched || into > _clingTouchInto)
            {
                _clingTouched = true;
                _clingTouchNormal = n;
                _clingTouchNormalY = normal.Y;
                _clingTouchInto = into;
            }
        }

        // ProcessBiped, before walking/jumping: Jump while clinging = wall jump (true: he jumped this frame)
        private bool WallClingInput()
        {
            if (!_clinging || !Controls.Jump.IsPressed)
            {
                return false;
            }
            ClingRelease();
            Flags1 |= PlayerFlags1.UsedJump;
            float up = IsPrimeHunter ? 0.35f : Fixed.ToFloat(Values.JumpSpeed); // as a jump (PlayerInput)
            Speed = new Vector3(_clingNormal.X * _clingJumpPush, up, _clingNormal.Z * _clingJumpPush);
            _gravityHold = 2;
            _timeSinceGrounded = 8 * 2;
            _clingKickTimer = _clingKickTime;
            _clingSinceJump = 0; // the hold rule's air time starts again from the wall jump
            WallJumps++;
            PlayHunterSfx(HunterSfx.Jump);
            return true;
        }

        // ProcessBiped, before the walk/strafe speed is added: just after a wall jump, steering back into the wall he
        // kicked off is ignored (holding forward into the wall would otherwise pull him straight back onto it)
        private Vector3 WallClingSteer(Vector3 speedDelta)
        {
            if (_clingKickTimer > 0)
            {
                float into = speedDelta.X * _clingNormal.X + speedDelta.Z * _clingNormal.Z;
                if (into < 0)
                {
                    speedDelta.X -= _clingNormal.X * into;
                    speedDelta.Z -= _clingNormal.Z * into;
                }
            }
            return speedDelta;
        }

        // ProcessMovement, after a knockback's speed: on the wall nothing moves him (and gravity is skipped there)
        private void WallClingHold()
        {
            _clingTouched = false;
            if (_clinging)
            {
                Speed = Vector3.Zero;
            }
        }

        // ProcessMovement, after the room collision and the grounded update: start, keep or end a cling
        private void WallClingAfterCollision()
        {
            bool standing = Flags1.TestFlag(PlayerFlags1.Standing);
            if (standing)
            {
                _clingLastNormal = Vector3.Zero; // landed: any wall again
                _clingChainCount = 0;
                _clingAirFrames = 0;
                _clingRose = false;
                _clingTopY = Position.Y;
                _clingSinceJump = 0;
            }
            else
            {
                if (_clingAirFrames < 10000)
                {
                    _clingAirFrames++;
                }
                if (_gravityHold == 1)
                {
                    _clingRose = true; // a jump this frame (PlayerInput's jump / a wall jump: gravity held for its first tick)
                    _clingSinceJump = 0;
                }
                else if (_clingSinceJump < 10000)
                {
                    _clingSinceJump++;
                }
                _clingTopY = MathF.Max(_clingTopY, Position.Y);
            }
            if (_clingWaitTimer > 0)
            {
                _clingWaitTimer--;
            }
            if (_clingKickTimer > 0 && (--_clingKickTimer == 0 || standing))
            {
                _clingKickTimer = 0;
            }
            if (_clinging)
            {
                _clingTimer--;
                int held = WallClingTime - _clingTimer;
                if (held > WallClingLongest)
                {
                    WallClingLongest = held;
                }
                if (_health == 0 || IsAltForm || IsMorphing || standing || _frozenTimer > 0)
                {
                    WallClingDrops++;
                    ClingRelease();
                }
                else if (_clingTimer <= 0)
                {
                    WallClingTimeouts++;
                    ClingRelease(); // falls from here (gravity is back next frame)
                }
            }
            else if (_clingTouched && _clingTouchInto > _clingMinInto && _clingWaitTimer == 0 && !IsBot
                && _health > 0 && !standing && !Flags1.TestFlag(PlayerFlags1.Grounded) && !IsAltForm && !IsMorphing
                && !IsUnmorphing && _frozenTimer == 0 && !Flags1.TestFlag(PlayerFlags1.UsedJumpPad)
                && _clingChainCount < WallClingChainLimit
                && (!HostWallClingJumpRule || _clingRose || _clingTopY - Position.Y >= WallClingMinDrop)
                && (!HostWallClingHold || Controls.Jump.IsDown && _clingSinceJump >= WallClingHoldDelay)
                && Vector3.Dot(_clingTouchNormal, _clingLastNormal) < _clingSameWall
                && WallClingTallEnough(_clingTouchNormal))
            {
                _clinging = true;
                _clingTimer = WallClingTime;
                _clingNormal = _clingTouchNormal;
                _clingLastNormal = _clingTouchNormal;
                WallClingNormalY = _clingTouchNormalY;
                WallClingStartAir = _clingAirFrames;
                WallClingStartRose = _clingRose;
                WallClingStartDrop = _clingTopY - Position.Y;
                _clingChainCount++;
                Speed = Vector3.Zero;
                WallClings++;
                _soundSource.PlaySfx(SfxId.LAND_METAL);
            }
            _clingTouched = false;
        }

        // the wall covers him from feet to head: a ray into it (against its normal) at each height meets a face standing
        // within the allowed angles and facing the same way as the one he touched
        private bool WallClingTallEnough(Vector3 n)
        {
            float bottom = Fixed.ToFloat(Values.MinPickupHeight) + _clingProbeInset;
            float top = Fixed.ToFloat(Values.MaxPickupHeight) - _clingProbeInset;
            float reach = Fixed.ToFloat(Values.BipedColRadius) + _clingProbeReach;
            for (int i = 0; i < 3; i++)
            {
                float y = i == 0 ? bottom : i == 1 ? (bottom + top) / 2 : top;
                Vector3 from = Position.AddY(y);
                var result = new CollisionResult();
                if (!CollisionDetection.CheckBetweenPoints(from, from - n * reach, TestFlags.Players, _scene, ref result)
                    || result.Plane.Y < WallClingMinNormalY || result.Plane.Y > WallClingMaxNormalY
                    || result.Plane.X * n.X + result.Plane.Z * n.Z < 0.95f)
                {
                    WallClingTooShort++;
                    return false;
                }
            }
            return true;
        }

        private void ClingRelease()
        {
            _clinging = false;
            _clingTimer = 0;
            _clingWaitTimer = _clingWait;
        }

        // recomp online (MphRecomp Net/ snapshots, Trooper abilities in network matches): the cling as the host has it
        private int _netClingMismatch = 0;

        public PlayerNetCling NetCling => new PlayerNetCling
        {
            Clinging = _clinging,
            Timer = (byte)Math.Clamp(_clingTimer, 0, 255),
            Chain = (byte)Math.Clamp(_clingChainCount, 0, 255),
            NormalX = (sbyte)Math.Clamp(MathF.Round(_clingNormal.X * 127), -127, 127),
            NormalZ = (sbyte)Math.Clamp(MathF.Round(_clingNormal.Z * 127), -127, 127)
        };

        // a client takes the host's cling: a puppet (pose) at once; the local player's own prediction normally clings
        // and lets go by itself from the relayed input, so only a mismatch that persists is corrected (as forms)
        public void NetApplyCling(in PlayerNetCling c, bool pose)
        {
            if (!HostWallCling)
            {
                return;
            }
            if (pose)
            {
                _clingChainCount = c.Chain;
            }
            if (c.Clinging == _clinging)
            {
                _netClingMismatch = 0;
                if (_clinging && pose)
                {
                    _clingTimer = c.Timer;
                }
                return;
            }
            if (!pose && (_netClingMismatch += NetApplyTicks) <= 10 * NetApplyScale) // (30 Hz snapshots' worth: PlayerNetPosition.cs)
            {
                return;
            }
            _netClingMismatch = 0;
            if (c.Clinging && _health > 0 && !IsAltForm)
            {
                float x = c.NormalX / 127f;
                float z = c.NormalZ / 127f;
                float len = MathF.Sqrt(x * x + z * z);
                _clingNormal = len > 1e-4f ? new Vector3(x / len, 0, z / len) : Vector3.Zero;
                _clingLastNormal = _clingNormal;
                _clinging = true;
                _clingTimer = c.Timer;
                _clingChainCount = c.Chain;
                Speed = Vector3.Zero;
            }
            else if (!c.Clinging)
            {
                ClingRelease();
            }
        }
    }

    // recomp: the cling pose on MphRead's own biped (Weavel: the Trooper's no-import look, owner 2026-10-09 "A with C's
    // lower crouch", extract_out/characters_mockups/weavel_cling): legs folded back onto the wall and the free arm up, the
    // legs turned to face away from the wall while the upper body keeps the aim, blended in and out over 6 drawn frames.
    // Draw only (PlayerDraw's drawBiped), off unless the app sets HostWallClingPose.
    public partial class PlayerEntity
    {
        public static bool HostWallClingPose { get; set; }

        // (joint, degrees about its own X, Y, Z: Weavel's rig, -weavelcling probe: Z bends hips/knees back, the
        // shoulder's -Y swings the arm back and -Z raises it)
        private static readonly (string Node, Vector3 Degrees)[] _clingPoseTurns =
        {
            ("L_hip", new Vector3(0, -10, 50)), ("R_hip", new Vector3(0, 10, 45)),
            ("L_knee", new Vector3(0, 0, 60)), ("R_knee", new Vector3(0, 0, 65)),
            ("L_shoulder", new Vector3(0, -75, -50)), ("L_elbow", new Vector3(0, 30, 0))
        };
        private const float _clingPoseStep = 1 / 6f;
        private float _clingPoseWeight = 0;
        private Vector3 _clingPoseNormal = Vector3.Zero;

        // drawBiped, after the two-layer AnimateNodes (model space, before the placement): the joint bends
        private void WallClingPoseModel(Model model)
        {
            // (a seat drawn by its own body, e.g. the native Prime trooper, has its own wall-hang clips: DS nodes untouched)
            bool on = HostWallClingPose && HostWallCling && !HostOwnBiped && _clinging && _health > 0 && _clingNormal != Vector3.Zero;
            if (on)
            {
                _clingPoseNormal = _clingNormal;
            }
            _clingPoseWeight = on ? MathF.Min(1, _clingPoseWeight + _clingPoseStep) : MathF.Max(0, _clingPoseWeight - _clingPoseStep);
            if (_clingPoseWeight <= 0 || !HostWallClingPose)
            {
                return;
            }
            float w = _clingPoseWeight;
            foreach ((string name, Vector3 deg) in _clingPoseTurns)
            {
                int index = BipedNodeIndex(model, name);
                if (index < 0)
                {
                    continue;
                }
                Matrix4 turn = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(deg.X * w))
                    * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(deg.Y * w))
                    * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(deg.Z * w));
                Matrix4 before = model.Nodes[index].Animation;
                TurnBipedSubtree(model, index, before.Inverted() * (turn * before));
            }
        }

        // drawBiped, after the placement (world space): the legs (Pelvis down) turned about the pelvis to face out of the
        // wall; the upper body (Spine_1 up) keeps facing the aim
        private void WallClingPoseWorld(Model model, Vector3 lateral)
        {
            if (_clingPoseWeight <= 0 || !HostWallClingPose)
            {
                return;
            }
            int pelvis = BipedNodeIndex(model, "Pelvis");
            float len = MathF.Sqrt(lateral.X * lateral.X + lateral.Z * lateral.Z);
            if (pelvis < 0 || len < 1e-5f)
            {
                return;
            }
            float ax = lateral.X / len, az = lateral.Z / len;
            Vector3 n = _clingPoseNormal;
            float angle = MathF.Atan2(ax * n.Z - az * n.X, ax * n.X + az * n.Z) * _clingPoseWeight;
            Matrix4 rot = Matrix4.CreateRotationY(angle);
            // (OpenTK's Y rotation turns one way or the other in row-vector use: take the one that brings the aim onto the wall)
            Vector3 target = Vector3.Lerp(new Vector3(ax, 0, az), n, _clingPoseWeight);
            Matrix4 alt = Matrix4.CreateRotationY(-angle);
            if (Vector3.Dot(Vector3.TransformNormal(new Vector3(ax, 0, az), alt), target)
                > Vector3.Dot(Vector3.TransformNormal(new Vector3(ax, 0, az), rot), target))
            {
                rot = alt;
            }
            Vector3 pivot = model.Nodes[pelvis].Animation.ExtractTranslation();
            TurnBipedSubtree(model, pelvis, Matrix4.CreateTranslation(-pivot) * rot * Matrix4.CreateTranslation(pivot));
        }

        // a joint and everything below it: Animation = Animation * delta (row vectors)
        public static void TurnBipedSubtree(Model model, int index, Matrix4 delta)
        {
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                int p = i;
                while (p != -1 && p != index)
                {
                    p = model.Nodes[p].ParentIndex;
                }
                if (p == index)
                {
                    model.Nodes[i].Animation *= delta;
                }
            }
        }

        private static int BipedNodeIndex(Model model, string name)
        {
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                if (model.Nodes[i].Name == name)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    // one seat's Wall Cling in an online snapshot (MphRecomp Net/; 5 bytes on the wire)
    public struct PlayerNetCling
    {
        public bool Clinging;
        public byte Timer; // frames left (WallClingTime = 150)
        public byte Chain; // clings since he last stood
        public sbyte NormalX, NormalZ; // the wall's horizontal normal x 127
    }
}
