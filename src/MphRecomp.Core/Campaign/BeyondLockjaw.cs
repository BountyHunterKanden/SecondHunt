using System;
using System.Runtime.CompilerServices;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Beyond Sylux's Lockjaw (owner 2026-10-17: "we still need to get lockjaw"): Prime 4's own alt form, the two helix
    // blades his model carries on its lockjaw_* joints (bodies/MP4SyluxLockjaw: Import/Bodies/BeyondBody part "lockjaw",
    // same skeleton and scale as bodies/MP4Sylux), drawn where MphRead draws vanilla Lockjaw (its alt transform) for a
    // Sylux seat wearing the Beyond skin, with Prime 4's own clips: its loop at rest, its forward roll on the move, its
    // dash at speed, leaning into turns. Drawing only: the sim is vanilla Lockjaw's.
    public static class BeyondLockjaw
    {
        public const string Id = "MP4SyluxLockjaw";
        public const string Loop = "lockJaw_loop_sylux";
        public const string Forward = "lockJaw_forward_sylux";
        public const string Dash = "lockJaw_dash_forward_sylux";
        public const string LeanLeft = "lockJaw_leanLeft_45_sylux";
        public const string LeanRight = "lockJaw_leanRight_45_sylux";
        const string Main = "lockjaw_main_C0_0_jnt";

        // speeds (world units / s) and the turn rate (radians / s) that pick the clips; MoveHold: a wanted clip held this
        // long before it plays (no flicker at a threshold)
        const float MoveMin = 0.6f, DashMin = 6f, TurnMin = 2.5f, MoveHold = 0.12f;

        // (its size next to vanilla Lockjaw: 1 = Prime 4's own, at Sylux's body scale)
        public static float Size = 1f;

        // the Beyond skin's Lockjaw, or null
        public static string? IdFor(string? skin) => String.Equals(skin, "MP4Sylux", StringComparison.OrdinalIgnoreCase) ? Id : null;

        public static bool Is(NativeBody body) => body.Clip(Loop) != null && body.Bone(Main) >= 0;

#pragma warning disable CA2255 // registers its map with BodyClips before any body loads; no other caller needed
        [ModuleInitializer]
        internal static void Init() => BodyClips.Register(b =>
        {
            if (!Is(b)) return null;
            Rest(b); // measured now, not first inside a pose (it poses the loop itself)
            Shape(b);
            return new BodyClips { Idle = Loop, AfterPose = (body, _) => Level(body) };
        });
#pragma warning restore CA2255

        // Its mesh lies flat on its joints' binds (both of Prime 4's skeletons: the model's and the gameplay character's,
        // lockjaw_main at rest unturned), but every Lockjaw clip turns lockjaw_main ~90 degrees about X (a fixed value, the
        // leans and the dash only a few degrees off it), which stood it on its edge (owner 10-17: "up and down vertically
        // instead of horizontally floating above the ground like it should be"). So the mesh is turned back by that 90
        // degrees in its joints' frames: lying flat, as the bind and the game have it, with the clips' bob, leans and
        // nose-down / up kept.
        // (the clips turn it ~86.5 degrees, not 90: the loop at rest, turned back by 90, still sat 3.5 degrees nose up and 1.4
        // rolled -- owner 10-17: "it looks tilted". So the rest is levelled exactly as well: Rest turns the loop's resting
        // up axis onto world up, about the main joint, in every clip, which keeps each clip's own tilt next to the rest:
        // the forward roll's few degrees nose down, the leans' ~10 degrees of roll)
        static readonly Matrix4 Flat = Matrix4.CreateRotationX(-MathF.PI / 2);
        static readonly ConditionalWeakTable<NativeBody, StrongBox<Matrix4>> _rests = new();

        static Matrix4 Rest(NativeBody body)
        {
            lock (_rests)
            {
                if (_rests.TryGetValue(body, out StrongBox<Matrix4>? box)) return box.Value;
                Matrix4 r = Matrix4.Identity;
                int main = body.Bone(Main);
                if (body.Clip(Loop) is HdGun.Anim loop && main >= 0)
                {
                    body.Pose(loop, 0);
                    Vector3 up = (Flat * body.World[main]).Row1.Xyz.Normalized();
                    Vector3 axis = Vector3.Cross(up, Vector3.UnitY);
                    float angle = MathF.Acos(Math.Clamp(Vector3.Dot(up, Vector3.UnitY), -1f, 1f));
                    if (axis.LengthSquared > 1e-10f) r = Matrix4.CreateFromAxisAngle(axis.Normalized(), angle);
                    if (Vector3.Dot(Vector3.TransformVector(up, r), Vector3.UnitY) < 0.999f)
                    {
                        r = Matrix4.CreateFromAxisAngle(axis.Normalized(), -angle);
                    }
                }
                _rests.Add(body, new StrongBox<Matrix4>(r));
                return r;
            }
        }

        // (and its mesh is built at an angle on its joints: its length 9.2 degrees nose down, its width 1.7 rolled, owner 10-17
        // after the joint was level: "lockjaw is still at an angled plane can we get it more parallel with the ground". Shape
        // turns the mesh, in its joints' frame, so its length and width (the main axes of its bind vertices) lie flat)
        static readonly ConditionalWeakTable<NativeBody, StrongBox<Matrix4>> _shapes = new();

        static Matrix4 Shape(NativeBody body)
        {
            lock (_shapes)
            {
                if (_shapes.TryGetValue(body, out StrongBox<Matrix4>? box)) return box.Value;
                Matrix4 r = Matrix4.Identity;
                HdGun g = body.Data;
                Vector3 mean = Vector3.Zero;
                int n = 0;
                foreach (HdGun.Material m in g.Materials)
                {
                    for (int v = 0; v < m.VertexCount; v++, n++)
                    {
                        int o = v * HdGun.FloatsPerVertex;
                        mean += new Vector3(m.Vertices[o], m.Vertices[o + 1], m.Vertices[o + 2]);
                    }
                }
                if (n > 2)
                {
                    mean /= n;
                    var cov = new Matrix3();
                    foreach (HdGun.Material m in g.Materials)
                    {
                        for (int v = 0; v < m.VertexCount; v++)
                        {
                            int o = v * HdGun.FloatsPerVertex;
                            Vector3 d = new Vector3(m.Vertices[o], m.Vertices[o + 1], m.Vertices[o + 2]) - mean;
                            cov.Row0 += d.X * d; cov.Row1 += d.Y * d; cov.Row2 += d.Z * d;
                        }
                    }
                    // its length (the widest spread, toward +Z) and its width (the next, toward +X)
                    Vector3 len = Vector3.UnitZ;
                    for (int it = 0; it < 200; it++) len = (cov * len).Normalized();
                    if (len.Z < 0) len = -len;
                    float l0 = Vector3.Dot(len, cov * len);
                    cov.Row0 -= l0 * len.X * len; cov.Row1 -= l0 * len.Y * len; cov.Row2 -= l0 * len.Z * len;
                    Vector3 wid = Vector3.UnitX;
                    for (int it = 0; it < 200; it++) wid = (cov * wid).Normalized();
                    wid = (wid - Vector3.Dot(wid, len) * len).Normalized();
                    if (wid.X < 0) wid = -wid;
                    Vector3 nrm = Vector3.Cross(len, wid);
                    var flatLen = new Vector3(len.X, 0, len.Z);
                    if (flatLen.LengthSquared > 1e-6f)
                    {
                        flatLen.Normalize();
                        Vector3 flatWid = Vector3.Cross(Vector3.UnitY, flatLen);
                        if (Vector3.Dot(flatWid, wid) < 0) flatWid = -flatWid;
                        Vector3 flatNrm = Vector3.Cross(flatLen, flatWid);
                        // row vectors: v * from^T * to
                        var from = new Matrix4(new Vector4(len, 0), new Vector4(wid, 0), new Vector4(nrm, 0), Vector4.UnitW);
                        var to = new Matrix4(new Vector4(flatLen, 0), new Vector4(flatWid, 0), new Vector4(flatNrm, 0), Vector4.UnitW);
                        r = Matrix4.Transpose(from) * to;
                    }
                }
                _shapes.Add(body, new StrongBox<Matrix4>(r));
                return r;
            }
        }

        static void Level(NativeBody body)
        {
            HdGun g = body.Data;
            int main = body.Bone(Main);
            if (main < 0) return;
            Matrix4 turn = Shape(body) * Flat;
            Matrix4 rest = Rest(body);
            Vector3 at = body.World[main].ExtractTranslation();
            Matrix4 about = Matrix4.CreateTranslation(-at) * rest * Matrix4.CreateTranslation(at);
            for (int i = 0; i < g.BoneCount; i++)
            {
                if (g.Names[i].StartsWith("lockjaw_", StringComparison.Ordinal)) body.World[i] = turn * body.World[i] * about;
            }
        }

        // the point drawn on the alt form's centre: its main joint (the middle of its blades, where its orb glows)
        static readonly ConditionalWeakTable<NativeBody, StrongBox<Vector3>> _pivots = new();

        static Vector3 Pivot(NativeBody body)
        {
            lock (_pivots)
            {
                if (_pivots.TryGetValue(body, out StrongBox<Vector3>? box)) return box.Value;
                Vector3 at = Vector3.Zero;
                if (body.Clip(Loop) is HdGun.Anim loop)
                {
                    body.Pose(loop, 0);
                    at = body.World[body.Bone(Main)].ExtractTranslation();
                }
                _pivots.Add(body, new StrongBox<Vector3>(at));
                return at;
            }
        }

        // model -> world: its middle on the alt form's (centre: MPH's alt collision sphere's, where MphRead draws vanilla
        // Lockjaw: it floats as vanilla's does), scaled as Sylux's body, turned as MphRead turns vanilla Lockjaw (turn: its
        // alt transform's rotation rows; model +Z = forward, +Y up, as MPH's)
        public static Matrix4 ModelMatrix(NativeBody body, Vector3 centre, Matrix4 turn)
        {
            turn.Row3 = new Vector4(0, 0, 0, 1);
            turn.Column3 = new Vector4(0, 0, 0, 1);
            if (turn.Row0.Xyz.LengthSquared < 1e-6f || turn.Row1.Xyz.LengthSquared < 1e-6f) turn = Matrix4.Identity;
            return Matrix4.CreateTranslation(-Pivot(body)) * Matrix4.CreateScale(body.Scale * Size) * turn * Matrix4.CreateTranslation(centre);
        }

        // its orb's place in the world after a pose drawn with model (its main joint: the core joint on it carries no mesh,
        // Prime 4 draws the orb as an effect there)
        public static Vector3 Core(NativeBody body, Matrix4 model) => (new Vector4(body.World[body.Bone(Main)].ExtractTranslation(), 1) * model).Xyz;

        // one seat's Lockjaw clips: the loop at rest, the forward roll moving, the dash fast, a lean while it turns
        public sealed class Animator
        {
            readonly NativeBody _body;
            readonly NativeBodyAnimator _a;
            Vector3? _last;
            float? _lastHeading;
            string _play = Loop, _want = Loop;
            float _wantFor;

            public Animator(NativeBody body, BodyClips map)
            {
                _body = body;
                _a = new NativeBodyAnimator(body, map);
            }

            public NativeBodyAnimator Clips => _a;
            public string Playing => _a.Playing;

            // after each sim step while the seat is in its alt form; forward: the alt transform's forward row
            public void Step(Vector3 position, Vector3 forward, float dt)
            {
                Vector3 v = _last is Vector3 l && dt > 0 ? (position - l) / dt : Vector3.Zero;
                _last = position;
                v.Y = 0;
                float heading = MathF.Atan2(forward.X, forward.Z);
                float turn = 0;
                if (_lastHeading is float h && dt > 0)
                {
                    float d = heading - h;
                    while (d > MathF.PI) d -= 2 * MathF.PI;
                    while (d < -MathF.PI) d += 2 * MathF.PI;
                    turn = d / dt;
                }
                _lastHeading = heading;
                float speed = v.Length;
                string want = speed < MoveMin ? Loop
                    : turn > TurnMin && _body.Clip(LeanLeft) != null ? LeanLeft
                    : turn < -TurnMin && _body.Clip(LeanRight) != null ? LeanRight
                    : speed >= DashMin && _body.Clip(Dash) != null ? Dash
                    : _body.Clip(Forward) != null ? Forward : Loop;
                if (want != _want)
                {
                    _want = want;
                    _wantFor = 0;
                }
                _wantFor += dt;
                if (_want != _play && _wantFor >= MoveHold) _play = _want;
                _a.Step(new BodyClips.Choice(_play, _play, true), dt);
            }
        }
    }
}
