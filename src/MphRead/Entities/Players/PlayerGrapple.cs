using System;
using System.Collections.Generic;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: Echoes' grapple beam in Echoes arenas with grapple points (docs/GRAPPLE.md section 7 is the full rule set
    // this follows; Core Arenas/EchoesGrapple.cs sets HostGrapplePoints on every room load: the room's points, or null =
    // none of this runs and the player is MphRead's own). Echoes' TweakPlayer values in MPH units (x 1.6/2.7, the
    // arenas' scale), speeds per 30 Hz tick:
    //   - aim: a tap of the grapple button picks the nearest usable point within 25 (3D) that's more than 2
    //     away level, inside her view (Echoes' on-screen lock circle) and in plain sight of her eyes.
    //   - fire: after the arm comes up the beam flies out at 30/s (60/s in the air) and catches the point.
    //   - pull: no gravity; she flies straight to the spot a rope length (10) under the point, 23-30/s.
    //   - swing: a fixed 3.3 s pendulum, top speed 23/s at the bottom; nothing she presses changes its speed. Move
    //     left/right turn the swing (35 degrees/s) and her view with it, as Echoes does.
    //   - view (owner 10-31, Corruption style): pulled or swinging, the aim stick moves the aim inside the view (25
    //     degrees either side, 15 up or down), the reticle and the arm cannon following it across the screen; held
    //     against the side edge, the view and the swing plane turn together at 35 degrees/s, as move left/right do
    //     (up or down, the view's pitch follows). The plane also turns onto the view from the catch (as a line).
    //   - the grab is a toggle (owner 10-31; Echoes holds the button): it stays on with the button up, a second tap
    //     lets go, and jump lets go while pulled or swinging.
    //   - release (Echoes' grapple mode 2): letting go mid-swing throws her on along the swing (jump force 13);
    //     letting go before the swing just drops the beam. No regrab wait, a new tap is needed.
    //   - breaks: the point out of sight or gone, morphing, frozen, hit, dying.
    // Sim code: plain float math on 60 Hz steps, sqrt only, no trig (replays and network play stay bit-exact).
    public partial class PlayerEntity
    {
        // the room being loaded is an Echoes arena with grapple points, in a match with the grapple on (else null)
        public static List<GrapplePointEntity>? HostGrapplePoints { get; set; }

        public enum GrappleState : byte
        {
            None,
            Firing,
            Pull,
            Swing
        }

        private const float GrappleScale = 1.6f / 2.7f;
        public const float GrappleDistance = 25 * GrappleScale;
        public const float GrappleRopeDefault = 10 * GrappleScale;
        // aim: more than this far away level (Echoes' orbit distance threshold); kept while the beam flies only from
        // GrappleKeepNearFlat to GrappleKeepFar level and under 70 degrees up or down (|dy| / level < tan 70)
        private const float GrappleNearFlat = 2 * GrappleScale;
        private const float GrappleKeepNearFlat = 1 * GrappleScale;
        private const float GrappleKeepFar = 30 * GrappleScale;
        private const float GrappleKeepSlope = 2.7474774f;
        // Echoes' lock circle (radius 180 of a 640x448 view, 55 degrees high) as a cone round her aim: sideways over
        // forward at most this
        private const float GrappleViewCone = 0.418f;
        // the arm coming up before the beam leaves (Echoes plays it as an animation; about its length)
        private const int GrappleFireDelay = 8;
        private const float GrappleBeamStep = 30 * GrappleScale / 60;
        private const float GrapplePullMax = 30 * GrappleScale / 30;
        private const float GrappleMaxVelocity = 23 * GrappleScale / 30;
        private const float GrapplePullDampen = 2 * GrappleScale;
        private const float GrapplePullClose = 0.5f * GrappleScale;
        public const float GrappleSwingSteps = 198; // 3.3 s
        // the rope pulls her back onto its length: 32/s per rope length off, scaled by how far below the point she is
        private const float GrappleRopeSpring = 32f / 30;
        // the turn: 35 degrees/s, one 60 Hz step's cos and sin (digital: MPH has no analog move)
        private const float GrappleTurnCos = 0.99994817f;
        private const float GrappleTurnSin = 0.010180911f;
        private const float GrappleTurnMinSq = 0.04f * GrappleScale * GrappleScale;
        // Echoes' release force 10000 x 13 on a 200 mass, one 60 Hz step: 10.83/s along the swing
        private const float GrappleJumpSpeed = 10000f * 13 / 200 / 60 * GrappleScale / 30;
        // the beam leaves from about her chest (biped Position is the feet)
        private static readonly Vector3 GrappleHandOffset = new Vector3(0, 0.9f, 0);

        // the PC sim test's grapple button (the real one is the scan bind)
        public bool GrappleInput { get; set; }
        // the PC sim test's turn: -1 left, 1 right (the real one is move left/right)
        public int GrappleTurnInput { get; set; }
        // the PC sim test's jump (the real one is the jump bind's press)
        public bool GrappleJumpInput { get; set; }

        public GrappleState Grapple { get; private set; }
        public GrapplePointEntity? GrapplePoint { get; private set; }
        // the beam's front: flying out while Firing, on the point after
        public Vector3 GrappleTip { get; private set; }
        public float GrappleRope { get; private set; }
        // steps into the swing's period (0 to GrappleSwingSteps)
        public float GrappleSwingTime { get; private set; }
        // the swing's turn axis (level, unit)
        private Vector3 _grappleAxis;
        private float _grappleBeam;
        private int _grappleDelay;
        // after letting go: the swing's level speed rides on to the landing, out of MPH's air drag (as a pad's push
        // does); her own air steering still works on top of it
        private Vector3 _grappleCarry;
        private int _grappleLastHealth;
        private bool _grappleInputPrev;
        // the view on the rope: the swing's level heading (unit) and the view's pitch as cos/sin; after letting go the
        // view eases back onto the aim for this many steps
        private Vector3 _grappleHeading;
        private float _grapplePitchCos;
        private float _grapplePitchSin;
        private int _grappleViewEase;
        // the aim pushed against the view's side edge this step: -1 left, 1 right (GrappleViewHold -> GrappleSteer)
        private int _grappleEdgeTurn;
        // how far the aim may leave the view: 25 degrees either side of the heading, 15 above or below the view's pitch
        private const float GrappleAimYawCos = 0.906307787f;
        private const float GrappleAimYawSin = 0.422618262f;
        private const float GrappleAimPitchCos = 0.965925826f;
        private const float GrappleAimPitchSin = 0.258819045f;
        // the share of the way the view moves each step when it eases back onto the aim after letting go
        private const float GrappleViewFollow = 0.15f;
        private const int GrappleViewEaseSteps = 30;

        // pulled or swinging: her own walking and jumping are off (PlayerInput's movement block)
        public bool GrappleSwinging => Grapple == GrappleState.Pull || Grapple == GrappleState.Swing;

        // the grapple holds her view (PlayerInput's aim leaves it alone) and the gun points right at the aim (PlayerProcess)
        private bool GrappleViewActive => HostGrapplePoints != null && (GrappleSwinging || _grappleViewEase > 0);

        // drops the beam; mid-swing that throws her on along the swing (Echoes' mode 2 release)
        public void GrappleRelease()
        {
            if (Grapple == GrappleState.None)
            {
                return;
            }
            if (Grapple == GrappleState.Swing && _health > 0 && GrapplePoint != null)
            {
                Vector3 d = Position - GrapplePoint.Position;
                Vector3 c = GrappleSwingDir(d);
                Vector3 flat = d.WithY(0);
                float dot = 1;
                float cl = c.Length;
                float fl = flat.Length;
                if (cl > 1e-6f && fl > 1e-6f)
                {
                    dot = MathF.Abs(Vector3.Dot(c, flat) / (cl * fl));
                }
                Speed += c * (GrappleJumpSpeed * dot);
            }
            if (GrappleSwinging)
            {
                _grappleCarry = Speed.WithY(0);
                _grappleViewEase = GrappleViewEaseSteps;
                _grappleEdgeTurn = 0;
            }
            Grapple = GrappleState.None;
            GrapplePoint = null;
        }

        private Vector3 GrappleHand => Position + GrappleHandOffset;
        private Vector3 GrappleEye => Position.AddY(Fixed.ToFloat(Values.AimYOffset));

        private bool GrappleInSight(GrapplePointEntity p)
        {
            var result = new CollisionResult();
            return !CollisionDetection.CheckBetweenPoints(GrappleEye, p.Position, TestFlags.Players, _scene, ref result);
        }

        private GrapplePointEntity? GrappleTarget()
        {
            List<GrapplePointEntity>? points = HostGrapplePoints;
            if (points == null)
            {
                return null;
            }
            Vector3 eye = GrappleEye;
            Vector3 aim = _facingVector;
            GrapplePointEntity? best = null;
            float bestDist = GrappleDistance * GrappleDistance;
            for (int i = 0; i < points.Count; i++)
            {
                GrapplePointEntity p = points[i];
                if (!p.Usable)
                {
                    continue;
                }
                Vector3 to = p.Position - Position;
                float d = to.LengthSquared;
                // Echoes' lock takes only a point it would keep (no more than 70 deg above or below her): without this a
                // point picked from nearly under it fired the beam and dropped it the next step (owner 2026-10-31 replay)
                if (d >= bestDist || to.WithY(0).LengthSquared <= GrappleNearFlat * GrappleNearFlat || !GrappleKept(p))
                {
                    continue;
                }
                Vector3 look = p.Position - eye;
                float forward = Vector3.Dot(look, aim);
                if (forward <= 0
                    || (look - aim * forward).LengthSquared > GrappleViewCone * GrappleViewCone * forward * forward)
                {
                    continue;
                }
                if (!GrappleInSight(p))
                {
                    continue;
                }
                best = p;
                bestDist = d;
            }
            return best;
        }

        // the HUD icon (App CampaignRenderer.GrappleHud.cs): the point a press would grab now, and whether a point is in sight
        public GrapplePointEntity? HostGrapplePick() => HostGrapplePoints == null || Grapple != GrappleState.None ? null : GrappleTarget();
        public bool HostGrappleSees(GrapplePointEntity p) => GrappleInSight(p);

        // test tools (-replaygrapple): each point as the pick and the beam's lock see it from here now, read only
        public string HostGrappleWhy()
        {
            List<GrapplePointEntity>? points = HostGrapplePoints;
            if (points == null)
            {
                return "no points";
            }
            var sb = new System.Text.StringBuilder();
            Vector3 eye = GrappleEye;
            for (int i = 0; i < points.Count; i++)
            {
                GrapplePointEntity p = points[i];
                Vector3 to = p.Position - Position;
                Vector3 look = p.Position - eye;
                float forward = Vector3.Dot(look, _facingVector);
                float off = forward > 0 ? (look - _facingVector * forward).Length / forward : 99;
                sb.Append($" [{i} d{to.Length:0.00} flat{to.WithY(0).Length:0.00} dy{to.Y:0.00} cone{off:0.000}"
                    + $"{(p.Usable ? "" : " UNUSABLE")}{(GrappleInSight(p) ? "" : " BLOCKED")}{(GrappleKept(p) ? "" : " NOTKEPT")}]");
            }
            return sb.ToString();
        }

        // the beam's lock while it flies (Echoes' orbit lock rules)
        private bool GrappleKept(GrapplePointEntity p)
        {
            Vector3 to = p.Position - Position;
            float flat = to.WithY(0).Length;
            return flat > GrappleKeepNearFlat && flat <= GrappleKeepFar && MathF.Abs(to.Y) < GrappleKeepSlope * flat;
        }

        // the swing's way at offset d from the point: across the rope, round the axis, which flips each half period
        private Vector3 GrappleSwingDir(Vector3 d)
        {
            float len = d.Length;
            if (len < 1e-6f)
            {
                return Vector3.Zero;
            }
            Vector3 axis = GrappleSwingTime < GrappleSwingSteps / 2 ? -_grappleAxis : _grappleAxis;
            return Vector3.Cross(d / len, axis);
        }

        // |sin(2 pi u)| without trig: folded onto a quarter turn, then the series to x^11 (error under 1e-7)
        private static float GrappleAbsSin(float u)
        {
            u -= MathF.Floor(u);
            if (u >= 0.5f)
            {
                u -= 0.5f;
            }
            if (u > 0.25f)
            {
                u = 0.5f - u;
            }
            float x = u * 6.2831853f;
            float x2 = x * x;
            return x * (1 - x2 / 6 * (1 - x2 / 20 * (1 - x2 / 42 * (1 - x2 / 72 * (1 - x2 / 110)))));
        }

        // turns a level vector one step right (+1) or left (-1); MPH's right of v is (-v.z, 0, v.x)
        private static Vector3 GrappleTurn(Vector3 v, int dir)
        {
            float s = GrappleTurnSin * dir;
            return new Vector3(v.X * GrappleTurnCos - v.Z * s, v.Y, v.Z * GrappleTurnCos + v.X * s);
        }

        // one 60 Hz step of the grapple; true = she is on the rope this step and gravity is off (PlayerInput)
        private bool GrappleStep()
        {
            // the scan bind is free in a match (G2); GrappleInput is the PC sim test's. A tap grabs, the next tap lets go.
            bool held = GrappleInput || Controls.Scan.IsDown;
            bool pressed = held && !_grappleInputPrev;
            _grappleInputPrev = held;
            // PlayerInput's movement block skips her own jump while she's on the rope, so this press is only ours
            bool jumped = GrappleSwinging && (GrappleJumpInput || Controls.Jump.IsPressed);
            if (Grapple != GrappleState.None)
            {
                GrapplePointEntity? point = GrapplePoint;
                if (pressed || jumped || point == null || !point.Usable || HostGrapplePoints == null
                    || !HostGrapplePoints.Contains(point) || _health <= 0 || _health < _grappleLastHealth
                    || _frozenTimer > 0 || IsAltForm || IsMorphing || !GrappleInSight(point)
                    || (Grapple == GrappleState.Firing && !GrappleKept(point)))
                {
                    GrappleRelease();
                    // the tap that let go doesn't grab again below
                    pressed = false;
                }
            }
            _grappleLastHealth = _health;
            if (_grappleViewEase > 0)
            {
                // after letting go: the view eases back onto the aim (MPH's own facing would snap the last 10 degrees)
                _grappleViewEase--;
                Vector3 f = _facingVector + (_gunVec1 - _facingVector) * GrappleViewFollow;
                float fl = f.Length;
                _facingVector = EquipInfo.Zoomed || fl < 1e-6f ? _gunVec1 : f / fl;
            }
            if (Grapple == GrappleState.None && _grappleCarry != Vector3.Zero)
            {
                if (Flags1.TestAny(PlayerFlags1.Standing | PlayerFlags1.UsedJumpPad) || _health <= 0 || IsAltForm)
                {
                    _grappleCarry = Vector3.Zero;
                }
                else
                {
                    // PlayerInput's drag ran on it this step (speed *= _walkFriction): give that part back
                    Speed += _grappleCarry * (1 - _walkFriction);
                    _walkFriction = 1;
                }
            }
            if (Grapple == GrappleState.None)
            {
                if (pressed && _health > 0 && _frozenTimer == 0 && !IsAltForm && !IsMorphing)
                {
                    GrapplePointEntity? target = GrappleTarget();
                    if (target != null)
                    {
                        Grapple = GrappleState.Firing;
                        GrapplePoint = target;
                        GrappleTip = GrappleHand;
                        GrappleRope = GrappleRopeDefault;
                        _grappleBeam = 0;
                        _grappleDelay = GrappleFireDelay;
                        _grappleCarry = Vector3.Zero;
                    }
                }
                return false;
            }
            Vector3 anchor = GrapplePoint!.Position;
            if (Grapple == GrappleState.Firing)
            {
                Vector3 hand = GrappleHand;
                Vector3 to = anchor - hand;
                float far = to.Length;
                if (_grappleDelay > 0)
                {
                    _grappleDelay--;
                }
                else
                {
                    _grappleBeam += Flags1.TestAny(PlayerFlags1.Standing) ? GrappleBeamStep : GrappleBeamStep * 2;
                }
                if (_grappleBeam < far)
                {
                    GrappleTip = hand + to * (_grappleBeam / far);
                    return false;
                }
                // caught: the swing turns round the level line to the point's right, the pull starts
                GrappleTip = anchor;
                Grapple = GrappleState.Pull;
                Vector3 level = (anchor - Position).WithY(0);
                Vector3 axis = new Vector3(-level.Z, 0, level.X);
                float al = axis.Length;
                // straight under it (the keep rule makes that rare): her facing's right
                _grappleAxis = al > 1e-6f ? axis / al : new Vector3(-_field74, 0, _field70).Normalized();
                // the view starts where she was looking; the swing plane turns onto it (GrappleChase)
                Vector3 look = _facingVector.WithY(0);
                float ll = look.Length;
                _grappleHeading = ll > 1e-6f ? look / ll : GrappleSwingHeading;
                float pl = MathF.Sqrt(ll * ll + _facingVector.Y * _facingVector.Y);
                _grapplePitchCos = pl > 1e-6f ? ll / pl : 1;
                _grapplePitchSin = pl > 1e-6f ? _facingVector.Y / pl : 0;
                _grappleViewEase = 0;
                _grappleEdgeTurn = 0;
            }
            if (Grapple == GrappleState.Pull)
            {
                Vector3 goal = anchor.AddY(-GrappleRope) - Position;
                float dist = goal.Length;
                if (dist >= GrapplePullClose)
                {
                    GrappleSteer();
                    GrappleChase(out _, out _);
                    float speed = GrappleMaxVelocity
                        + (GrapplePullMax - GrappleMaxVelocity) * MathF.Min(dist / GrapplePullDampen, 1);
                    Speed = goal * (speed / dist);
                }
                else
                {
                    Grapple = GrappleState.Swing;
                    GrappleSwingTime = GrappleSwingSteps / 4;
                }
            }
            if (Grapple == GrappleState.Swing)
            {
                GrappleSwingTime += 1;
                if (GrappleSwingTime >= GrappleSwingSteps)
                {
                    GrappleSwingTime -= GrappleSwingSteps;
                }
                Vector3 d = Position - anchor;
                float len = d.Length;
                Vector3 v = GrappleSwingDir(d) * (GrappleAbsSin(GrappleSwingTime / GrappleSwingSteps) * GrappleMaxVelocity);
                if (len > 1e-6f)
                {
                    float off = Math.Clamp((len - GrappleRope) / GrappleRope, -1, 1);
                    v -= d * (MathF.Abs(d.Y / len) * GrappleRopeSpring * off);
                }
                int turn = GrappleSteer();
                Vector3 flat = d.WithY(0);
                // carry her round the point with the plane's turns (a step's move is Speed / 2)
                bool carry = flat.LengthSquared > GrappleTurnMinSq;
                if (turn != 0 && carry)
                {
                    v += (GrappleTurn(flat, turn) - flat) * 2;
                }
                if (GrappleChase(out float tc, out float ts) && carry)
                {
                    v += (GrappleRotate(flat, tc, ts) - flat) * 2;
                }
                Speed = v;
            }
            GrappleViewHold();
            _walkFriction = 1;
            Flags1 &= ~PlayerFlags1.Standing;
            return true;
        }

        // the swing's level heading: toward the point at the catch, the axis' left (MPH's right of v is (-v.z, 0, v.x))
        private Vector3 GrappleSwingHeading => new Vector3(_grappleAxis.Z, 0, -_grappleAxis.X);

        private static Vector3 GrappleRotate(Vector3 v, float c, float s)
        {
            return new Vector3(v.X * c - v.Z * s, v.Y, v.Z * c + v.X * s);
        }

        // move left/right, or the aim held against the view's side edge: the swing plane, her view and her aim turn
        // together, 35 degrees/s (Echoes; owner 10-31: the view turns at the swing's rate)
        private int GrappleSteer()
        {
            int turn = Math.Clamp(GrappleTurnInput + _grappleEdgeTurn
                + (Controls.MoveRight.IsDown ? 1 : 0) - (Controls.MoveLeft.IsDown ? 1 : 0), -1, 1);
            _grappleEdgeTurn = 0;
            if (turn != 0)
            {
                _grappleAxis = GrappleTurn(_grappleAxis, turn);
                _grappleHeading = GrappleTurn(_grappleHeading, turn);
                _gunVec1 = GrappleTurn(_gunVec1, turn).Normalized();
            }
            return turn;
        }

        // the swing plane follows her view (owner 10-31): it turns toward the view's line at up to 35 degrees/s, the
        // short way round (the plane swings both ways, so a view behind her is already in it). Gives the turn made.
        private bool GrappleChase(out float c, out float s)
        {
            Vector3 a = _grappleAxis;
            // the axis wanted: the view heading's right (MPH's right of v is (-v.z, 0, v.x)), either way round
            Vector3 t = new Vector3(-_grappleHeading.Z, 0, _grappleHeading.X);
            c = a.X * t.X + a.Z * t.Z;
            s = a.X * t.Z - a.Z * t.X;
            if (c < 0)
            {
                c = -c;
                s = -s;
            }
            if (c < GrappleTurnCos)
            {
                c = GrappleTurnCos;
                s = s < 0 ? -GrappleTurnSin : GrappleTurnSin;
            }
            else if (s == 0)
            {
                return false;
            }
            Vector3 r = GrappleRotate(a, c, s);
            float rl = r.Length;
            if (rl > 1e-6f)
            {
                _grappleAxis = r / rl;
            }
            return true;
        }

        // PlayerInput's UpdateAimFacing (each aim change) and GrappleStep: true = the grapple has her view. Pulled or
        // swinging, the aim moves inside the view, up to 25 degrees off the heading level; pushed against that edge it
        // stops there and the view turns (Corruption's turn at the screen's edge, at the swing's 35 degrees/s, via
        // GrappleSteer), and past 15 degrees up or down the view's pitch follows it. The
        // view looks along the heading at that pitch (zoomed: down the aim, as MPH's zoom does).
        private bool GrappleViewHold()
        {
            if (HostGrapplePoints == null)
            {
                return false;
            }
            if (_grappleViewEase > 0)
            {
                // GrappleStep eases the view back onto the aim
                return true;
            }
            if (!GrappleSwinging)
            {
                return false;
            }
            Vector3 h = _grappleHeading;
            float fwd = _gunVec1.X * h.X + _gunVec1.Z * h.Z;
            float side = _gunVec1.Z * h.X - _gunVec1.X * h.Z;
            float flat = MathF.Sqrt(fwd * fwd + side * side);
            // past the side edge (beyond float noise from the last clamp): the aim stops at the edge, and the push turns
            // the view and the swing at their own rate next GrappleStep
            if (flat > 1e-6f && (fwd <= 0 || MathF.Abs(side) * GrappleAimYawCos - GrappleAimYawSin * fwd > 1e-5f * flat))
            {
                _grappleEdgeTurn = side < 0 ? -1 : 1;
                fwd = flat * GrappleAimYawCos;
                side = (side < 0 ? -flat : flat) * GrappleAimYawSin;
                _gunVec1 = new Vector3(h.X * fwd - h.Z * side, _gunVec1.Y, h.Z * fwd + h.X * side);
            }
            float a = flat;
            float b = _gunVec1.Y;
            float pc = _grapplePitchCos;
            float ps = _grapplePitchSin;
            if (EquipInfo.Zoomed)
            {
                pc = a;
                ps = b;
            }
            else
            {
                // the aim's pitch measured from the view's
                float relX = a * pc + b * ps;
                float relY = b * pc - a * ps;
                if (relY * GrappleAimPitchCos > GrappleAimPitchSin * relX)
                {
                    pc = a * GrappleAimPitchCos + b * GrappleAimPitchSin;
                    ps = b * GrappleAimPitchCos - a * GrappleAimPitchSin;
                }
                else if (-relY * GrappleAimPitchCos > GrappleAimPitchSin * relX)
                {
                    pc = a * GrappleAimPitchCos - b * GrappleAimPitchSin;
                    ps = b * GrappleAimPitchCos + a * GrappleAimPitchSin;
                }
            }
            float pl = MathF.Sqrt(pc * pc + ps * ps);
            if (pl > 1e-6f)
            {
                _grapplePitchCos = pc / pl;
                _grapplePitchSin = ps / pl;
            }
            _aimPosition = CameraInfo.Position + _gunVec1 * Fixed.ToFloat(Values.AimDistance);
            _facingVector = EquipInfo.Zoomed ? _gunVec1
                : new Vector3(h.X * _grapplePitchCos, _grapplePitchSin, h.Z * _grapplePitchCos);
            return true;
        }

        // ---- draw side (not sim state: replays and network play don't see it) ----

        // where the beam leaves her left wrist in the last draw (third person / other players), null when the arm wasn't
        // posed (no grapple, or the biped wasn't animated): CampaignRenderer.GrappleFx.cs draws the beam from it
        // (a host body drawn on its own skeleton -- CampaignSuit, the native bodies -- raises its own arm by HostGrapplePose
        // toward HostGrapplePoseTip and sets this to its own wrist after this draw)
        public Vector3? HostGrappleHand { get; set; }
        // how far the arm is up (0 down .. 1 at the tip: up in 5 frames, down in 10), the tip it points at, and MPH's
        // L_shoulder in world space as this draw posed it (null when the arm wasn't posed)
        public float HostGrapplePose => _grapplePose;
        public Vector3 HostGrapplePoseTip => _grapplePoseTip;
        public Vector3? HostGrappleShoulder { get; private set; }
        private float _grapplePose;
        private Vector3 _grapplePoseTip;

        // Echoes' left-arm grapple: the arm comes up straight and points at the beam's tip, the right (gun) arm free.
        // Done on the drawn joints in world space after the clips (as the Wall Cling's leg turn), so every hunter on the
        // MPH biped rig gets it (shared node names L_shoulder / L_elbow / L_wrist), no clip of its own needed.
        private void GrapplePoseWorld(Model model)
        {
            HostGrappleHand = null;
            HostGrappleShoulder = null;
            if (Grapple != GrappleState.None)
            {
                _grapplePoseTip = GrappleTip;
                _grapplePose = MathF.Min(1, _grapplePose + 0.2f);
            }
            else
            {
                _grapplePose = MathF.Max(0, _grapplePose - 0.1f);
            }
            if (_grapplePose <= 0 || HostGrapplePoints == null)
            {
                return;
            }
            int shoulder = BipedNodeIndex(model, "L_shoulder");
            int elbow = BipedNodeIndex(model, "L_elbow");
            int wrist = BipedNodeIndex(model, "L_wrist");
            if (shoulder < 0 || elbow < 0 || wrist < 0)
            {
                return;
            }
            Vector3 sh = model.Nodes[shoulder].Animation.ExtractTranslation();
            HostGrappleShoulder = sh;
            Vector3 el = model.Nodes[elbow].Animation.ExtractTranslation();
            Vector3 wr = model.Nodes[wrist].Animation.ExtractTranslation();
            // straighten the elbow, then turn the whole arm at the shoulder onto the tip
            TurnBipedSubtree(model, elbow, TurnOnto(el, wr - el, el - sh, _grapplePose));
            wr = model.Nodes[wrist].Animation.ExtractTranslation();
            TurnBipedSubtree(model, shoulder, TurnOnto(sh, wr - sh, _grapplePoseTip - sh, _grapplePose));
            HostGrappleHand = model.Nodes[wrist].Animation.ExtractTranslation();
        }

        // a turn about `pivot` taking direction a `weight` of the way onto b (row vectors)
        private static Matrix4 TurnOnto(Vector3 pivot, Vector3 a, Vector3 b, float weight)
        {
            if (a.LengthSquared < 1e-10f || b.LengthSquared < 1e-10f)
            {
                return Matrix4.Identity;
            }
            a.Normalize();
            b.Normalize();
            Vector3 axis = Vector3.Cross(a, b);
            float sin = axis.Length;
            if (sin < 1e-6f)
            {
                return Matrix4.Identity;
            }
            axis /= sin;
            float angle = MathF.Atan2(sin, Vector3.Dot(a, b)) * weight;
            // (OpenTK's rotations turn one way or the other in row-vector use: take the one that brings a toward b)
            Matrix4 rot = Matrix4.CreateFromAxisAngle(axis, angle);
            Matrix4 alt = Matrix4.CreateFromAxisAngle(axis, -angle);
            if (Vector3.Dot(Vector3.TransformNormal(a, alt), b) > Vector3.Dot(Vector3.TransformNormal(a, rot), b))
            {
                rot = alt;
            }
            return Matrix4.CreateTranslation(-pivot) * rot * Matrix4.CreateTranslation(pivot);
        }
    }
}
