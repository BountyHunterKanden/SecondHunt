using System;
using System.Runtime.CompilerServices;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // The Trooper's Scout Drone on its own Prime 1 body (docs/CHARACTERS_AND_SKINS.md D10/D22: "the Prime 1 pirate drone"):
    // the Space Pirate drone from the user's Prime disc (Metroid3.pak ANCS 5e058399: AlphaDrone grey/orange, Gamma_Drone
    // purple; recipes/bodies/MP1AlphaDrone, MP1GammaDrone), drawn where MphRead flies the drone in place of its Psycho Bit
    // stand-in, with its own clips: hovering, floating the way it moves, its blaster arms out while it fires, hits.
    // Draw only: the drone's flight, size and shots are the simulation's (PlayerScoutDrone.cs), the same with or without it.
    public static class DroneBody
    {
        public const string Ready = "B_ready_drone";
        // world units per model unit: its round body (~1.3 model units) about half Weavel's alt-form ball (0.8) across, so
        // the spike behind it and the blaster arms it opens to fire stay clear of the alt form's close third-person camera
        // (0.5 filled the screen; owner to tune by eye)
        public const float Scale = 0.3f;
        // MPH's aim tilt limit (sin 2896/4096: 45 degrees): the drone pitches toward the aim up to it
        const float PitchSin = 2896 / 4096f;
        // the sim's unfold / fold lengths (PlayerScoutDrone, frames at 60 per second): the clips stretch to them
        const int UnfoldFrames = 32;
        const int FoldFrames = 24;
        // the cutting laser's length (PlayerDroneLaser, frames): its clip stretches to it
        const int LaserFrames = 134;
        public const string LaserClip = "A_cuttinglaser_drone";
        // world units per second before it floats the way it moves (else it hovers)
        const float MoveMin = 0.9f;
        // a new float direction must hold this long before it plays (no flicker between two)
        const float MoveHold = 0.12f;

#pragma warning disable CA2255 // registers the drone's map with BodyClips before any body loads; no other caller needed
        [ModuleInitializer]
        internal static void Init() => BodyClips.Register(For);
#pragma warning restore CA2255

        static readonly ConditionalWeakTable<NativeBody, StrongBox<Vector3>> _pivots = new();

        static BodyClips? For(NativeBody body)
        {
            if (!Is(body)) return null;
            Pivot(body);
            return new BodyClips { Idle = Ready };
        }

        // every clip the Animator plays (bodytest checks they're all there)
        public static readonly string[] Clips =
        {
            Ready, "B_floatloopf_drone", "B_floatloopb_drone", "B_floatleft_drone", "B_floatright_drone", "B_floatup_drone",
            "B_floatdown_drone", "A_intoshootloop_drone", "A_shootloop_drone", "A_outofshootloop_drone", "R_hitf_drone",
            LaserClip,
        };

        public static bool Is(NativeBody body) => body.Clip(Ready) != null && body.Clip("B_floatloopf_drone") != null;

        // a trooper skin's drone: the purple Gamma drone with the (purple) Wave Trooper, the grey/orange Alpha drone with
        // the others
        public static string IdFor(string? skin)
            => String.Equals(skin, "MP1WaveTrooper", StringComparison.OrdinalIgnoreCase) ? "MP1GammaDrone" : "MP1AlphaDrone";

        // the point drawn on the drone's centre: its body's own centre (Skeleton_Root in its hover's first frame)
        static Vector3 Pivot(NativeBody body)
        {
            lock (_pivots)
            {
                if (_pivots.TryGetValue(body, out StrongBox<Vector3>? box)) return box.Value;
                Vector3 at = Vector3.Zero;
                int bone = body.Bone(BodyImporterRoot);
                if (bone >= 0 && body.Clip(Ready) is HdGun.Anim ready)
                {
                    body.Pose(ready, 0);
                    at = body.World[bone].ExtractTranslation();
                }
                _pivots.Add(body, new StrongBox<Vector3>(at));
                return at;
            }
        }

        const string BodyImporterRoot = "Skeleton_Root";

        // model -> world: the body's centre on the drone's centre, scaled, turned to face `facing` (level) and pitched
        // toward `aim` (model +Z = forward, +Y up)
        public static Matrix4 ModelMatrix(NativeBody body, Vector3 center, Vector3 facing, Vector3 aim)
        {
            var lateral = new Vector3(facing.X, 0, facing.Z);
            lateral = lateral.LengthSquared > 1e-12f ? lateral.Normalized() : Vector3.UnitZ;
            float sin = aim.LengthSquared > 1e-12f ? Math.Clamp(aim.Normalized().Y, -PitchSin, PitchSin) : 0;
            float cos = MathF.Sqrt(1 - sin * sin);
            Vector3 forward = lateral * cos + Vector3.UnitY * sin;
            Vector3 side = Vector3.Cross(Vector3.UnitY, lateral).Normalized();
            Vector3 up = Vector3.Cross(forward, side);
            var turn = Matrix4.Identity;
            turn.Row0.Xyz = side;
            turn.Row1.Xyz = up;
            turn.Row2.Xyz = forward;
            return Matrix4.CreateTranslation(-Pivot(body)) * Matrix4.CreateScale(Scale) * turn * Matrix4.CreateTranslation(center);
        }

        // one seat's drone clips: the laser over hits over shots over moving over hovering
        public sealed class Animator
        {
            readonly NativeBody _body;
            readonly NativeBodyAnimator _a;
            Vector3? _last;
            string _move = Ready, _want = Ready;
            float _wantFor;
            int _state;         // the sim's burst state last step
            int _burst;         // bursts seen (each its own clip keys)
            int _hits;
            ushort _sinceHit = UInt16.MaxValue;
            int _laser;         // lasers seen (each its own clip key)
            int _laserFrame = -1;

            public Animator(NativeBody body, BodyClips map)
            {
                _body = body;
                _a = new NativeBodyAnimator(body, map);
            }

            public NativeBodyAnimator Clips => _a;
            public string Playing => _a.Playing;

            // after each sim step while the drone flies (p in alt form with its Scout Drone)
            public void Step(PlayerEntity p, float dt)
            {
                Vector3 c = p.DroneDrawCenter;
                Vector3 v = _last is Vector3 l && dt > 0 ? (c - l) / dt : Vector3.Zero;
                _last = c;
                // a new hit: MphRead's damage clock dropped back
                ushort since = p.DroneTimeSinceDamage;
                if (since < _sinceHit) _hits++;
                _sinceHit = since;
                // the cutting laser, as the sim runs it (PlayerDroneLaser): its whole clip from its first frame, never cut
                // by a hit (the beam comes on at the clip's own DamageOn)
                int lf = p.DroneLaserFrame;
                if (lf >= 0 && _body.Clip(LaserClip) is HdGun.Anim laser)
                {
                    if (_laserFrame < 0 || lf < _laserFrame) _laser++;
                    _laserFrame = lf;
                    float rate = laser.Duration > 0 ? laser.Duration / (LaserFrames / 60f) : 1;
                    _a.Step(new BodyClips.Choice($"laser{_laser}", LaserClip, false, Rate: rate, Snap: true), dt);
                    return;
                }
                _laserFrame = -1;
                HdGun.Anim? hit = _body.Clip("R_hitf_drone");
                if (since != UInt16.MaxValue && hit != null && since / 60f < hit.Duration)
                {
                    _a.Step(new BodyClips.Choice("hit" + _hits, hit.Name, false), dt);
                    return;
                }
                // the blaster burst, as the sim plays it (PlayerScoutDrone: 1 unfolding, 2 firing, 3 folding): each clip
                // starts when its state does, without the cross-fade (blending the crossed arms of two poses bone by bone
                // tangles them), timed to the state's length so the arms are out exactly when the shots start
                int state = p.DroneFireState;
                if (state != 0)
                {
                    if (state != _state) _burst += state == 1 ? 1 : 0;
                    _state = state;
                    (string clip, int frames) = state switch
                    {
                        1 => ("A_intoshootloop_drone", UnfoldFrames),
                        2 => ("A_shootloop_drone", 0),
                        _ => ("A_outofshootloop_drone", FoldFrames),
                    };
                    HdGun.Anim? anim = _body.Clip(clip);
                    float rate = frames > 0 && anim != null && anim.Duration > 0 ? anim.Duration / (frames / 60f) : 1;
                    _a.Step(new BodyClips.Choice($"burst{_burst}:{state}", clip, state == 2, Rate: rate, Snap: true), dt);
                    return;
                }
                _state = 0;
                // floating the way it moves (relative to where it faces), held a moment before it changes
                string want = MoveClip(p.DroneDrawFacing, v);
                if (want != _want)
                {
                    _want = want;
                    _wantFor = 0;
                }
                _wantFor += dt;
                if (_want != _move && (_wantFor >= MoveHold || _move == Ready)) _move = _want;
                _a.Step(new BodyClips.Choice(_move, _move, true), dt);
            }

            // the drone's world velocity in its own frame -> its float clip (model +X = its left)
            string MoveClip(Vector3 facing, Vector3 v)
            {
                Vector3 side = Vector3.Cross(Vector3.UnitY, facing);
                float fwd = Vector3.Dot(v, facing), left = Vector3.Dot(v, side), up = v.Y;
                float a = MathF.Abs(fwd), b = MathF.Abs(left), u = MathF.Abs(up);
                if (MathF.Max(a, MathF.Max(b, u)) < MoveMin) return Ready;
                if (u > a && u > b) return up > 0 ? "B_floatup_drone" : "B_floatdown_drone";
                if (a >= b) return fwd > 0 ? "B_floatloopf_drone" : "B_floatloopb_drone";
                return left > 0 ? "B_floatleft_drone" : "B_floatright_drone";
            }
        }
    }
}
