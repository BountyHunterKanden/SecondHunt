using System;
using System.Collections.Generic;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: Echoes' spider ball on the Echoes arenas' spider tracks, for every alt form (docs/SPIDER_BALL.md; owner
    // 2026-11-06). Core Arenas/EchoesSpiderBall.cs sets HostSpiderBall on every room load: on only in a match in an Echoes
    // arena whose collision has track faces (CollisionFlags.SpiderTrack, Echoes' bit 61), else none of this runs.
    // Echoes' CMorphBall rule set, in MPH units (x 1.6/2.7, the arenas' scale), speeds per 30 Hz tick:
    //   - engage: the spider button held (HostSpiderButton) in alt form, not locked out by damage.
    //   - find: the closest track point within reach (Echoes: 2.1 from a 0.7 ball, so 1.4 past its surface; ours is
    //     measured past each alt form's own radius). The track's "up" is ball centre minus that point.
    //   - stick: no gravity; the form is pulled onto the surface (Echoes sets 4 x its gravity toward it every frame,
    //     which closes any gap at once) and pushed out to its contact distance (radius + 0.2).
    //   - move: the move input steers over the surface. Forward is the view's forward laid on the surface, or the
    //     surface's own "up" where the view looks straight into it (a wall). Echoes' input ramp (full above 0.4) is
    //     digital here like MPH's own moves; speed follows the input at SpiderFollow a step up to the form's top speed.
    //     With no input and slow enough it locks still, as Echoes does.
    //   - edges: none. No track in reach, it falls as normal.
    //   - leave: let go of the button; damage (a short lockout); a boost launches it off along the track's up.
    // Sim code: plain float math on 60 Hz steps, sqrt only, no trig (replays and network play stay bit-exact).
    public partial class PlayerEntity
    {
        // the room being loaded is an Echoes arena with spider track, in a match (set by Core on every room load)
        public static bool HostSpiderBall { get; set; }

        // the spider ball button this step (MphRecomp CampaignButtons.Spider: pad L2, mouse right, the touch toggle)
        public bool HostSpiderButton { get; set; }

        // the drone's way onto the track (owner 2026-11-06, docs/SPIDER_BALL.md choice 6): its spider buttons are its laser,
        // so it latches on by itself when driven straight at a track (SpiderDroneHeld), and the fly button lets go.
        // Attached, it can't fly (PlayerInput skips ScoutDroneHover).
        // the share of a straight line at the track the move input must be (cos ~32 degrees: driving along a rail or past
        // it doesn't count) and for how many steps in a row
        private const float SpiderDroneAim = 0.85f;
        private const int SpiderDroneSteps = 8;
        // after the fly button lets go: off this long, so the rise carries it clear first
        private const int SpiderDroneLetGo = 30;
        private int _spiderDroneToward;

        private const float SpiderScale = 1.6f / 2.7f;
        // how far past the form's surface it finds track (Echoes 2.1 - 0.7) and where it touches (Echoes 0.2)
        private const float SpiderReach = 1.4f * SpiderScale;
        private const float SpiderContact = 0.2f * SpiderScale;
        // the share of the way the speed over the surface moves toward the input's each step
        private const float SpiderFollow = 0.12f;
        // Echoes locks the ball still under 6.5/s with no input
        private const float SpiderLockSpeed = 6.5f * SpiderScale / 30;
        // the pull onto the surface: at most this per tick (it closes the gap inside reach in a few steps)
        private const float SpiderPullMax = 1.5f * SpiderScale;
        // hit: the spider ball is off this many steps (Echoes' damage lockout)
        private const int SpiderDamageLock = 30;
        // after a boost launch: off this long, so the launch carries it clear before the button can catch the track again
        private const int SpiderBoostLock = 24;
        // Echoes' SpiderBallBoostScalar (mp2tweak twbl.txt:121)
        private const float SpiderBoostScalar = 1.7f;

        public bool SpiderAttached { get; private set; }
        // the track's "up" under the form while attached (unit)
        public Vector3 SpiderNormal { get; private set; } = Vector3.UnitY;
        // the track point under the form, and how far the form's centre is from it (the effects' line: PlayerSpiderLook)
        public Vector3 SpiderPoint { get; private set; }
        public float SpiderDistance { get; private set; }
        private int _spiderLock;
        private int _spiderLastHealth;
        private bool _spiderBoostPrev;
        // Echoes' rails (CMorphBall::ComputeSpiderBallForce, Prime's rule): on a rail the stick only picks which way along
        // it; the way it picked holds while the stick doesn't change (so a bend doesn't flip it). A rail is track where
        // probes one form across either side of the touch point find none (SpiderRail)
        private const float SpiderRailMin = 0.1f;
        private Vector3 _spiderRailMove;
        private int _spiderRailStick;
        // dev (-spiderball): this step's rail direction, zero off a rail
        public Vector3 DevSpiderRail { get; private set; }

        // the track faces of the loaded room, as triangles in world space (built on first use per room)
        private static object? _spiderRoom;
        // the loaded room has track faces (the app's touch SPIDER toggle shows only then; read on the UI thread)
        public static bool HostSpiderTrackHere { get; private set; }
        // dev check only (-spiderball): the level forward used for steering in place of the view's, which a headless run doesn't
        // place behind the ball. Null in play.
        public static Vector3? DevSpiderSteer { get; set; }
        private static readonly List<SpiderTri> _spiderTris = new List<SpiderTri>();

        private readonly struct SpiderTri
        {
            public readonly Vector3 A, B, C, Normal, Min, Max;

            public SpiderTri(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
            {
                A = a;
                B = b;
                C = c;
                Normal = normal;
                Min = Vector3.ComponentMin(Vector3.ComponentMin(a, b), c);
                Max = Vector3.ComponentMax(Vector3.ComponentMax(a, b), c);
            }
        }

        private static void SpiderBuild(Scene scene)
        {
            if (scene.Room == null || ReferenceEquals(_spiderRoom, scene.Room))
            {
                return;
            }
            _spiderRoom = scene.Room;
            _spiderTris.Clear();
            foreach (CollisionInstance inst in scene.Room.RoomCollision)
            {
                if (inst.Info.FirstHunt || inst.Info is not MphCollisionInfo info)
                {
                    continue;
                }
                foreach (CollisionData data in info.Data)
                {
                    if (!data.Flags.TestFlag(CollisionFlags.SpiderTrack) || data.PointIndexCount < 3)
                    {
                        continue;
                    }
                    Vector3 normal = info.Planes[data.PlaneIndex].Xyz;
                    int start = data.PointStartIndex;
                    Vector3 a = info.Points[info.PointIndices[start]] + inst.Translation;
                    for (int i = 1; i + 1 < data.PointIndexCount; i++)
                    {
                        Vector3 b = info.Points[info.PointIndices[start + i]] + inst.Translation;
                        Vector3 c = info.Points[info.PointIndices[start + i + 1]] + inst.Translation;
                        _spiderTris.Add(new SpiderTri(a, b, c, normal));
                    }
                }
            }
            HostSpiderTrackHere = _spiderTris.Count > 0;
        }

        // the number of track triangles in the loaded room (the dev check)
        public static int SpiderTrackCount(Scene scene)
        {
            SpiderBuild(scene);
            return _spiderTris.Count;
        }

        // a track triangle's centre and face normal (the dev check, -spiderball)
        public static (Vector3 A, Vector3 B, Vector3 C) SpiderTrackCorners(Scene scene, int index)
        {
            SpiderBuild(scene);
            SpiderTri t = _spiderTris[index];
            return (t.A, t.B, t.C);
        }

        public static (Vector3 Centre, Vector3 Normal) SpiderTrackFace(Scene scene, int index)
        {
            SpiderBuild(scene);
            SpiderTri t = _spiderTris[index];
            return ((t.A + t.B + t.C) / 3, t.Normal);
        }

        // the closest point of a triangle (Ericson, Real-Time Collision Detection 5.1.5)
        private static Vector3 SpiderClosest(Vector3 p, in SpiderTri t)
        {
            Vector3 ab = t.B - t.A, ac = t.C - t.A, ap = p - t.A;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return t.A;
            Vector3 bp = p - t.B;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return t.B;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return t.A + ab * (d1 / (d1 - d3));
            Vector3 cp = p - t.C;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return t.C;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return t.A + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return t.B + (t.C - t.B) * ((d4 - d3) / (d4 - d3 + d5 - d6));
            float denom = 1 / (va + vb + vc);
            return t.A + ab * (vb * denom) + ac * (vc * denom);
        }

        private Vector3 SpiderCentre => Position.AddY(Fixed.ToFloat(Values.AltColYPos));

        // the closest track point within reach of the form's centre, with its distance and "up"; false: none
        private bool SpiderFind(Vector3 centre, float reach, out Vector3 point, out float dist, out Vector3 normal)
        {
            point = Vector3.Zero;
            normal = Vector3.UnitY;
            dist = 0;
            float best = reach * reach;
            int found = -1;
            for (int i = 0; i < _spiderTris.Count; i++)
            {
                SpiderTri t = _spiderTris[i];
                if (centre.X < t.Min.X - reach || centre.X > t.Max.X + reach || centre.Y < t.Min.Y - reach
                    || centre.Y > t.Max.Y + reach || centre.Z < t.Min.Z - reach || centre.Z > t.Max.Z + reach)
                {
                    continue;
                }
                Vector3 q = SpiderClosest(centre, t);
                float d = (centre - q).LengthSquared;
                if (d < best)
                {
                    best = d;
                    point = q;
                    found = i;
                }
            }
            if (found < 0)
            {
                return false;
            }
            dist = MathF.Sqrt(best);
            // on the face (or nearly) the face's own normal, else the way out from the edge it's nearest
            normal = dist > 1e-4f ? (centre - point) / dist : _spiderTris[found].Normal;
            if (Vector3.Dot(normal, _spiderTris[found].Normal) < 0)
            {
                // behind the face: the track only holds from its front
                return false;
            }
            return true;
        }

        // the probes' directions on the surface (8, 45 degrees apart) and their doubled angles (the axis sum below)
        private static readonly (float X, float Y)[] _spiderRailProbe =
        {
            (1, 0), (0.70710677f, 0.70710677f), (0, 1), (-0.70710677f, 0.70710677f),
            (-1, 0), (-0.70710677f, -0.70710677f), (0, -1), (0.70710677f, -0.70710677f),
        };
        private static readonly int[] _spiderRailCone = { 7, 0, 1, 3, 4, 5 };
        private static readonly (float X, float Y)[] _spiderRailDouble =
        {
            (1, 0), (0, 1), (-1, 0), (0, -1), (1, 0), (0, 1), (-1, 0), (0, -1),
        };

        // the rail's direction at the touch point (unit, on the surface), or zero on broad track (a wall, a floor): from
        // the touch point raised to the contact distance, probes one form-width out in 8 directions on the surface look
        // for track under them
        private static Vector3 SpiderRail(Vector3 point, Vector3 n, float radius)
        {
            Vector3 a = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 t1 = Vector3.Cross(n, a).Normalized();
            Vector3 t2 = Vector3.Cross(n, t1);
            float h = radius + SpiderContact;
            float d = radius * 2;
            Vector3 lift = point + n * h;
            Span<bool> hit = stackalloc bool[8];
            for (int i = 0; i < 8; i++)
            {
                Vector3 probe = lift + (t1 * _spiderRailProbe[i].X + t2 * _spiderRailProbe[i].Y) * d;
                hit[i] = SpiderFindStatic(probe, h * SpiderRailProbeReach);
            }
            // a rail along probe k's axis: both probes across it miss, and track lies along it (k or k + 4 weigh 2, the
            // diagonals beside them 1). A broad surface, or a wall's edge, has a probe across every axis.
            int best = -1, bestScore = 0;
            for (int k = 0; k < 4; k++)
            {
                if (hit[k + 2] || hit[(k + 6) % 8])
                {
                    continue;
                }
                int score = (hit[k] ? 2 : 0) + (hit[k + 4] ? 2 : 0) + (hit[(k + 7) % 8] ? 1 : 0) + (hit[k + 1] ? 1 : 0)
                    + (hit[k + 3] ? 1 : 0) + (hit[(k + 5) % 8] ? 1 : 0);
                if (score > bestScore)
                {
                    best = k;
                    bestScore = score;
                }
            }
            if (best < 0)
            {
                return Vector3.Zero;
            }
            // the axis from the doubled angles of the hits about it, halved (no trig)
            float sx = 0, sy = 0;
            foreach (int o in _spiderRailCone)
            {
                int i = (best + o) % 8;
                if (hit[i])
                {
                    float w = o == 0 || o == 4 ? 2 : 1;
                    sx += _spiderRailDouble[i].X * w;
                    sy += _spiderRailDouble[i].Y * w;
                }
            }
            float sl = MathF.Sqrt(sx * sx + sy * sy);
            if (sl < 1e-4f)
            {
                return t1 * _spiderRailProbe[best].X + t2 * _spiderRailProbe[best].Y;
            }
            float ax = 1 + sx / sl, ay = sy / sl;
            float al = MathF.Sqrt(ax * ax + ay * ay);
            return al < 1e-4f ? t2 : (t1 * ax + t2 * ay) / al;
        }

        // a probe finds track this far off (x the contact distance): a probe past a rail's edge is further
        private const float SpiderRailProbeReach = 1.15f;

        private static bool SpiderFindStatic(Vector3 centre, float reach)
        {
            float best = reach * reach;
            for (int i = 0; i < _spiderTris.Count; i++)
            {
                SpiderTri t = _spiderTris[i];
                if (centre.X < t.Min.X - reach || centre.X > t.Max.X + reach || centre.Y < t.Min.Y - reach
                    || centre.Y > t.Max.Y + reach || centre.Z < t.Min.Z - reach || centre.Z > t.Max.Z + reach)
                {
                    continue;
                }
                Vector3 q = SpiderClosest(centre, t);
                if ((centre - q).LengthSquared < best && Vector3.Dot(centre - q, t.Normal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // bots (owner 2026-11-06 "bots now"; docs/SPIDER_BALL.md S5): the arenas' nav has no links over the track, so a
        // bot holds the button in alt form only while its next node (or its target) is above it: touching a track wall
        // or column on the way, it climbs toward it. It lets go when the goal isn't above, and after SpiderBotStall steps
        // without getting nearer (then waits SpiderBotRest before trying again), so a track that leads nowhere doesn't keep it.
        private const int SpiderBotStall = 45, SpiderBotRest = 90;
        private const float SpiderBotAbove = 1f;
        private float _spiderBotBest = Single.MaxValue;
        private int _spiderBotStalled, _spiderBotRest;

        private bool SpiderBotHeld()
        {
            if (_spiderBotRest > 0)
            {
                _spiderBotRest--;
                return false;
            }
            Vector3? goal = AiData?.HostNodePos ?? AiData?.HostTargetPlayer?.Position;
            Vector3 centre = SpiderCentre;
            if (!IsAltForm || goal is not Vector3 g || g.Y < centre.Y + (SpiderAttached ? 0 : SpiderBotAbove))
            {
                _spiderBotBest = Single.MaxValue;
                _spiderBotStalled = 0;
                return false;
            }
            float dist = (g - centre).LengthSquared;
            if (!SpiderAttached || dist < _spiderBotBest - 0.01f)
            {
                _spiderBotBest = MathF.Min(_spiderBotBest, dist);
                _spiderBotStalled = 0;
                return true;
            }
            if (++_spiderBotStalled < SpiderBotStall) return true;
            _spiderBotBest = Single.MaxValue;
            _spiderBotStalled = 0;
            _spiderBotRest = SpiderBotRest;
            return false;
        }

        // the drone: on while attached until the fly button; not attached, on once the move input has pointed straight at
        // track in reach for SpiderDroneSteps steps
        private bool SpiderDroneHeld()
        {
            if (!IsAltForm || IsMorphing)
            {
                _spiderDroneToward = 0;
                return false;
            }
            if (SpiderAttached)
            {
                if (Controls.Jump.IsPressed || TouchJumpPressed)
                {
                    _spiderLock = SpiderDroneLetGo;
                    _spiderDroneToward = 0;
                    return false;
                }
                return true;
            }
            float radius = Fixed.ToFloat(Values.AltColRadius);
            Vector3 centre = SpiderCentre;
            SpiderSteerAxes(out Vector3 fwdH, out Vector3 rightH);
            float fb = MoveForwardAxis, lr = MoveRightAxis; // the d-pad's, or the stick's (360-degree stick, PlayerStickPush.cs)
            Vector3 dir = fwdH * fb + rightH * lr;
            float dl = dir.Length;
            if (dl < 1e-4f || !SpiderFind(centre, radius + SpiderReach, out Vector3 point, out float dist, out _) || dist < 1e-4f)
            {
                _spiderDroneToward = 0;
                return false;
            }
            float aim = Vector3.Dot(dir / dl, (point - centre) / dist);
            _spiderDroneToward = aim >= SpiderDroneAim ? _spiderDroneToward + 1 : 0;
            return _spiderDroneToward >= SpiderDroneSteps;
        }

        // the view's forward and right (level) the alt form steers by
        private void SpiderSteerAxes(out Vector3 fwdH, out Vector3 rightH)
        {
            if (Values.AltFormStrafe != 0)
            {
                fwdH = new Vector3(_field70, 0, _field74);
                rightH = new Vector3(-_field78, 0, -_field7C);
            }
            else
            {
                fwdH = new Vector3(_altRollFbX, 0, _altRollFbZ);
                rightH = new Vector3(-_altRollLrX, 0, -_altRollLrZ);
            }
            if (DevSpiderSteer is Vector3 dev)
            {
                fwdH = dev;
                rightH = new Vector3(-dev.Z, 0, dev.X);
            }
        }

        private void SpiderDetach()
        {
            SpiderAttached = false;
            SpiderNormal = Vector3.UnitY;
            _spiderRailMove = Vector3.Zero;
            DevSpiderRail = Vector3.Zero;
        }

        // the step's spider ball, where gravity would run (PlayerInput's gravity block); true: attached (no gravity)
        private bool SpiderStep()
        {
            if (_spiderLock > 0)
            {
                _spiderLock--;
            }
            if (_health < _spiderLastHealth)
            {
                _spiderLock = SpiderDamageLock;
            }
            _spiderLastHealth = _health;
            SpiderBuild(_scene);
            bool boosting = Flags1.TestFlag(PlayerFlags1.Boosting);
            bool boosted = boosting && !_spiderBoostPrev;
            _spiderBoostPrev = boosting;
            bool held = HostScoutDrone ? _spiderLock == 0 && SpiderDroneHeld() : IsBot ? SpiderBotHeld() : HostSpiderButton;
            if (!held || !IsAltForm || IsMorphing || _health <= 0 || _frozenTimer > 0 || _spiderLock > 0)
            {
                SpiderDetach();
                return false;
            }
            float radius = Fixed.ToFloat(Values.AltColRadius);
            Vector3 centre = SpiderCentre;
            if (!SpiderFind(centre, radius + SpiderReach, out Vector3 point, out float dist, out Vector3 n))
            {
                SpiderDetach();
                return false;
            }
            if (boosted && SpiderAttached)
            {
                // Echoes' spider boost: off the track along its up, at the boost's speed x 1.7
                float boost = MathF.Max(Speed.Length, Fixed.ToFloat(Values.BoostSpeedMin));
                Speed = n * (boost * SpiderBoostScalar);
                _spiderLock = SpiderBoostLock;
                SpiderDetach();
                return false;
            }
            SpiderAttached = true;
            SpiderNormal = n;
            SpiderPoint = point;
            SpiderDistance = dist;
            // steering: the view's forward and right (level), laid on the surface
            SpiderSteerAxes(out Vector3 fwdH, out Vector3 rightH);
            float into = Vector3.Dot(fwdH, n);
            Vector3 up = Vector3.UnitY - n * n.Y;
            // looking into the surface, forward climbs it (a wall seen face on: forward is up the wall)
            Vector3 sf = fwdH - n * into + (into < 0 ? up * -into : -up * into);
            float sfl = sf.Length;
            Vector3 sr = rightH - n * Vector3.Dot(rightH, n);
            if (sfl > 1e-4f)
            {
                sf /= sfl;
                sr -= sf * Vector3.Dot(sr, sf);
            }
            float srl = sr.Length;
            if (srl > 1e-4f)
            {
                sr /= srl;
            }
            float fb = MoveForwardAxis, lr = MoveRightAxis; // the d-pad's, or the stick's (360-degree stick, PlayerStickPush.cs)
            bool input = fb != 0 || lr != 0;
            if (ParalyzerStunned || Flags2.TestFlag(PlayerFlags2.AltAttack) && Hunter != Hunter.Noxus)
            {
                input = false;
                fb = lr = 0;
            }
            Vector3 dir = sf * fb + sr * lr;
            float dl = dir.Length;
            if (dl > 1)
            {
                dir /= dl;
            }
            // on a rail: only along it, at full speed, the way the stick points along it (or, the stick unchanged, the
            // way it was going round a bend)
            Vector3 rail = SpiderRail(point, n, radius);
            DevSpiderRail = rail;
            // the d-pad direction it points (the stick's nearest of eight), so a small wobble of the stick holds through a bend
            int stick = ((Controls.MoveUp.IsDown ? 1 : 0) - (Controls.MoveDown.IsDown ? 1 : 0) + 1) * 3
                + (Controls.MoveRight.IsDown ? 1 : 0) - (Controls.MoveLeft.IsDown ? 1 : 0) + 1;
            if (rail != Vector3.Zero && input)
            {
                float sign = 0;
                if (stick == _spiderRailStick && _spiderRailMove != Vector3.Zero)
                {
                    sign = Vector3.Dot(rail, _spiderRailMove) >= 0 ? 1 : -1;
                }
                else
                {
                    float along1 = Vector3.Dot(dir, rail);
                    if (MathF.Abs(along1) >= SpiderRailMin)
                    {
                        sign = along1 > 0 ? 1 : -1;
                    }
                }
                dir = rail * sign;
                _spiderRailMove = dir;
            }
            else
            {
                _spiderRailMove = Vector3.Zero;
            }
            _spiderRailStick = input ? stick : -1;
            float top = Fixed.ToFloat(Values.AltFormStrafe != 0 ? Values.WalkSpeedCap : Values.AltMinHSpeed);
            Vector3 along = Speed - n * Vector3.Dot(Speed, n);
            along += (dir * top - along) * SpiderFollow;
            if (!input && along.LengthSquared <= SpiderLockSpeed * SpiderLockSpeed)
            {
                along = Vector3.Zero;
            }
            // onto the surface: position moves Speed / 2 a step, so twice the gap closes it in one
            float gap = dist - (radius + SpiderContact);
            float pull = gap > 0 ? MathF.Min(gap * 2, SpiderPullMax) : 0;
            Speed = along - n * pull;
            _walkFriction = 1;
            return true;
        }

        // after the step's collision: the form sits at its contact distance off the track, standing on it
        private void SpiderAfterCollision()
        {
            if (!SpiderAttached)
            {
                return;
            }
            float radius = Fixed.ToFloat(Values.AltColRadius);
            Vector3 centre = SpiderCentre;
            if (!SpiderFind(centre, radius + SpiderReach, out Vector3 point, out float dist, out Vector3 n))
            {
                SpiderDetach();
                return;
            }
            float contact = radius + SpiderContact;
            if (dist < contact)
            {
                Position += n * (contact - dist);
                dist = contact;
            }
            SpiderNormal = n;
            SpiderPoint = point;
            SpiderDistance = dist;
            if (dist <= contact + 0.05f)
            {
                Flags1 |= PlayerFlags1.Standing;
                _fieldC0 = n;
            }
        }

        // the alt form camera's goal while attached (UpdateCameraThird1). Prime has no spider camera of its own (its rooms
        // place hint cameras), and the alt camera's slow branch holds its height, so a climb went out of view. On a wall
        // the camera sits out from the wall, face on, at its usual distance and height (Echoes' spider rooms' hints do
        // the same); on a floor or under a ceiling it keeps its way round but follows the ball's height (above, or below
        // under a ceiling)
        private Vector3 SpiderCameraPos(Vector3 posVec, float dist, float height)
        {
            if (!SpiderAttached || MorphCamera != null || _jumpPadControlLock > 0)
            {
                return posVec;
            }
            Vector3 n = SpiderNormal;
            Vector3 target = CameraInfo.Target;
            if (MathF.Abs(n.Y) < 0.7f)
            {
                Vector3 outH = new Vector3(n.X, 0, n.Z).Normalized();
                return new Vector3(target.X + outH.X * dist, target.Y + height, target.Z + outH.Z * dist);
            }
            return posVec.WithY(target.Y + (n.Y > 0 ? height : -height));
        }

        // HandleCollision: the track's own faces don't push an attached form (SpiderAfterCollision holds it off them)
        private bool SpiderIgnores(in CollisionResult result)
        {
            return SpiderAttached && result.Flags.TestFlag(CollisionFlags.SpiderTrack);
        }

        // the alt model's draw offset while attached: a form that stands on the track turns about its own origin (its
        // feet), so it moves to keep its centre on the collision centre. The rollers turn about their centre already.
        private Vector3 SpiderDrawOffset => SpiderAttached && Hunter != Hunter.Samus && Hunter != Hunter.Spire
            ? (Vector3.UnitY - SpiderNormal) * Fixed.ToFloat(Values.AltColYPos) : Vector3.Zero;

        // v on the plane of n (unit), or fallback when v points along n
        private static Vector3 SpiderOnPlane(Vector3 v, Vector3 n, Vector3 fallback)
        {
            Vector3 f = v - n * Vector3.Dot(v, n);
            float fl = f.Length;
            return fl > 1e-4f ? f / fl : fallback;
        }

        // Kanden's Stinglarva on a track (UpdateStinglarvaSegments off it): the same head-led chain and wiggle, but
        // "up" is the track's up and every segment is seated on the track under it, so the body lies along the track
        // the way it lies on the floor instead of hanging off the head by world gravity
        private void SpiderStinglarvaSegments()
        {
            const int cycle = 13 * 2;
            Vector3 n = SpiderNormal;
            float yPos = Fixed.ToFloat(Values.AltColYPos);
            float reach = Fixed.ToFloat(Values.AltColRadius) + SpiderReach;
            float angle = 359f * (_scene.FrameCount % cycle) / (cycle - 1);
            float factor = 0.3f * MathF.Sin(MathHelper.DegreesToRadians(angle)) * _hSpeedMag;
            Vector3 facing = SpiderFacing(new Vector3(_field80, 0, _field84));
            Vector3 dir = Speed.LengthSquared > 0.02f
                ? SpiderOnPlane(Speed + new Vector3(_field80, 0, _field84) / 4, n, facing) : facing;
            Vector3 prev = SpiderOnPlane(_kandenSegMtx[0].Row2.Xyz, n, dir);
            if (Vector3.Dot(dir, prev) < -0.5f)
            {
                prev = dir; // turned right round (or just attached): no slow swing through the track
            }
            dir = SpiderOnPlane(prev + 0.3f * (dir - prev), n, dir);
            Vector3 side = Vector3.Cross(n, dir).Normalized();
            _kandenSegPos[0] = SpiderCentre - n * yPos + side * factor;
            _kandenSegMtx[0] = GetTransformMatrix(dir, n);
            _kandenSegMtx[0].Row3.Xyz = _kandenSegPos[0];
            Vector3 prevUp = n;
            for (int i = 1; i < _kandenSegPos.Length; i++)
            {
                angle += 85;
                while (angle >= 360)
                {
                    angle -= 360;
                }
                factor = 0.12f * MathF.Sin(MathHelper.DegreesToRadians(angle)) * _hSpeedMag;
                Matrix4 prevMtx = _kandenSegMtx[i - 1];
                Vector3 seg = _kandenSegPos[i] + prevMtx.Row0.Xyz * factor;
                Vector3 back = _kandenSegPos[i - 1] - seg;
                Vector3 segDir = back.LengthSquared > 1e-8f ? back.Normalized() : prevMtx.Row2.Xyz;
                float dot = Vector3.Dot(segDir, prevMtx.Row2.Xyz);
                if (dot < Fixed.ToFloat(2896)) // within 45 degrees of the segment ahead, as off the track
                {
                    Vector3 axis = Vector3.Cross(segDir, prevMtx.Row2.Xyz);
                    float mag = axis.Length;
                    if (mag > 1e-5f)
                    {
                        axis /= mag;
                        float turn = MathF.Atan2(mag, dot) - MathHelper.DegreesToRadians(45);
                        segDir = Vector3.Transform(segDir, Quaternion.FromAxisAngle(axis, turn));
                    }
                    else
                    {
                        segDir = prevMtx.Row2.Xyz;
                    }
                }
                seg = _kandenSegPos[i - 1] - segDir * KandenAltNodeDistances[i - 1];
                // seat it: its own centre at the head's height off the track, its feet toward the track
                Vector3 up = prevUp;
                if (SpiderFind(seg + prevUp * yPos, reach, out Vector3 q, out _, out Vector3 ni))
                {
                    seg = q + ni * (SpiderDistance - yPos);
                    up = ni;
                }
                back = _kandenSegPos[i - 1] - seg;
                segDir = SpiderOnPlane(back, up, SpiderOnPlane(prevMtx.Row2.Xyz, up, dir));
                _kandenSegPos[i] = seg;
                _kandenSegMtx[i] = GetTransformMatrix(segDir, up);
                _kandenSegMtx[i].Row3.Xyz = seg;
                prevUp = up;
            }
        }

        // dev check only (-spiderball): how far Kanden's worst body segment sits off the head's height above the track
        // (0 = the body lies on the track; 9 = a segment with no track under it)
        public float DevKandenSegGap()
        {
            float yPos = Fixed.ToFloat(Values.AltColYPos);
            float worst = 0;
            for (int i = 1; i < _kandenSegPos.Length; i++)
            {
                Vector3 centre = _kandenSegPos[i] + _kandenSegMtx[i].Row1.Xyz * yPos;
                worst = MathF.Max(worst, SpiderFind(centre, 3, out _, out float d, out _) ? MathF.Abs(d - SpiderDistance) : 9);
            }
            return worst;
        }

        // the facing laid on the track (unit), for the forms that stand or spin on it
        private Vector3 SpiderFacing(Vector3 facing)
        {
            Vector3 n = SpiderNormal;
            Vector3 f = facing - n * Vector3.Dot(facing, n);
            float fl = f.Length;
            if (fl > 1e-4f)
            {
                return f / fl;
            }
            // facing straight into the track: its up's way along the surface, or any line on it
            f = Vector3.UnitY - n * n.Y;
            fl = f.Length;
            return fl > 1e-4f ? f / fl : Vector3.Normalize(Vector3.Cross(n, Vector3.UnitX));
        }
    }
}
