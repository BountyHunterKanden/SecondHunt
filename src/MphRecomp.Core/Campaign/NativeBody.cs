using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // A native Retro body (Import/Bodies/BodyImporter: bodies/<Name>/body.bin in the guns' format + body.cfg) posed on
    // its own skeleton by its own clips, for a multiplayer character drawn the way its own game draws it (the Trooper on
    // the Prime 1 pirate rig, D22; Dark Samus on hers, D18). Pose math as GunViewmodel's: local = R * T, world = local *
    // parent; skin = InvBind * world. Model space is MPH's Y-up with the character facing +Z (body.bin's conversion);
    // ModelMatrix scales it to MPH's player height and stands it where MphRead stands the player.
    public sealed class NativeBody
    {
        public readonly HdGun Data;
        public readonly string Dir;
        public readonly float Height, Feet;   // body.cfg: the bind mesh's height and lowest point (model units)
        public readonly string Game;
        // body.cfg "speed": each clip's own travel speed across the floor (model units per second)
        public readonly Dictionary<string, float> Speeds = new(StringComparer.Ordinal);
        public readonly Matrix4[] World;
        readonly Quaternion[] _rot;
        readonly Vector3[] _trans;

        // MPH's player height (PlayerValues: MaxPickupHeight - MinPickupHeight, 1.6 for every hunter) -- the body is drawn
        // a little taller than its pickup box, as the hunters' own models are
        public const float PlayerHeight = 1.7f;

        // the Trooper (the Prime 1 pirate rig, B_ready_pirate) drawn at his Prime 1 size next to the hunters (owner
        // 2026-10-15): MP1's rooms place troopers 1.5x the size of Samus's own stand-in relative to their binds, so fitted
        // to PlayerHeight like the others he idled at 1/1.5 of it (top 1.33, head 0.66); at 1.5x his top is ~2.0, like
        // Spire's. Draw only (the hitbox is the sim's, the same cylinder for everyone): ModelMatrix and the walk rate;
        // Scale (the first-person cannon's size) is unchanged. Owner 10-16: 1.5x is "oppressively bigger", find a middle
        // ground; owner picked 1.3x by eye in a lineup with every hunter (top ~1.73, Noxus/Sylux height). Live steps 1x..1.5x
        // in the gun-adjust pause (TrooperSizeToggle.cs).
        public static float TrooperSize = 1.3f;
        readonly bool _pirate;
        public float Size => _pirate ? TrooperSize : 1;
        public float DrawScale => Scale * Size;

        NativeBody(string dir, HdGun data, float height, float feet, string game)
        {
            Dir = dir;
            Data = data;
            Height = height;
            Feet = feet;
            Game = game;
            _pirate = data.Anims.ContainsKey("B_ready_pirate");
            World = new Matrix4[data.BoneCount];
            _rot = new Quaternion[data.BoneCount];
            _trans = new Vector3[data.BoneCount];
            for (int i = 0; i < World.Length; i++) World[i] = Matrix4.CreateTranslation(data.Bind[i]);
        }

        public static string BinPath(string dir) => Path.Combine(dir, "body.bin");

        public static bool Exists(string dir) => File.Exists(BinPath(dir));

        public static NativeBody Load(string dir)
        {
            HdGun data = HdGun.Load(BinPath(dir));
            float height = 0, feet = 0;
            string game = "";
            var speeds = new Dictionary<string, float>(StringComparer.Ordinal);
            string cfg = Path.Combine(dir, "body.cfg");
            if (File.Exists(cfg))
            {
                foreach (string line in File.ReadAllLines(cfg))
                {
                    string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length < 2) continue;
                    if (t[0] == "height") height = Single.Parse(t[1], CultureInfo.InvariantCulture);
                    else if (t[0] == "feet") feet = Single.Parse(t[1], CultureInfo.InvariantCulture);
                    else if (t[0] == "game") game = t[1];
                    else if (t[0] == "speed" && t.Length >= 3) speeds[t[1]] = Single.Parse(t[2], CultureInfo.InvariantCulture);
                }
            }
            if (height <= 0) throw new InvalidDataException($"{dir}: body.cfg has no height");
            var body = new NativeBody(dir, data, height, feet, game);
            foreach ((string k, float v) in speeds) body.Speeds[k] = v;
            return body;
        }

        public HdGun.Anim? Clip(string name) => Data.Anims.TryGetValue(name, out HdGun.Anim? a) ? a : null;

        // the pose of clip a at time t, blended with clip b at time tb by weight wb (0 = a alone): rotations slerped,
        // translations lerped per bone, then the world matrices
        public void Pose(HdGun.Anim a, float t, HdGun.Anim? b = null, float tb = 0, float wb = 0)
        {
            HdGun g = Data;
            for (int i = 0; i < g.BoneCount; i++)
            {
                g.Sample(a, t, i, out _rot[i], out _trans[i]);
                if (b != null && wb > 0)
                {
                    g.Sample(b, tb, i, out Quaternion r2, out Vector3 p2);
                    _rot[i] = Quaternion.Slerp(_rot[i], r2, wb);
                    _trans[i] = Vector3.Lerp(_trans[i], p2, wb);
                }
            }
            BuildWorld();
        }

        // the pose of the first n clips at their times mixed by weight (each one slerped in by its share of the weight so
        // far; BodyClips.Lean's direction blend, cross-fades included)
        public void PoseMix(HdGun.Anim[] clips, float[] times, float[] weights, int n)
        {
            HdGun g = Data;
            for (int i = 0; i < g.BoneCount; i++)
            {
                float sum = 0;
                for (int k = 0; k < n; k++)
                {
                    if (weights[k] <= 0) continue;
                    g.Sample(clips[k], times[k], i, out Quaternion r, out Vector3 p);
                    sum += weights[k];
                    float s = weights[k] / sum;
                    _rot[i] = s >= 1 ? r : Quaternion.Slerp(_rot[i], r, s);
                    _trans[i] = s >= 1 ? p : Vector3.Lerp(_trans[i], p, s);
                }
            }
            BuildWorld();
        }

        void BuildWorld()
        {
            HdGun g = Data;
            foreach (int i in g.Order)
            {
                Matrix4 local = Matrix4.CreateFromQuaternion(_rot[i]) * Matrix4.CreateTranslation(_trans[i]);
                World[i] = g.Parent[i] >= 0 ? local * World[g.Parent[i]] : local;
            }
        }

        // after Pose: put one bone at a new world matrix (model space) and carry everything under it along, from their own
        // local poses (BodyClips.AfterPose: e.g. Dark Samus's cannon arm held forward, D14)
        public void SetWorld(int bone, Matrix4 world)
        {
            HdGun g = Data;
            World[bone] = world;
            var moved = new bool[g.BoneCount];
            moved[bone] = true;
            foreach (int i in g.Order)
            {
                int parent = g.Parent[i];
                if (i == bone || parent < 0 || !moved[parent]) continue;
                moved[i] = true;
                World[i] = Matrix4.CreateFromQuaternion(_rot[i]) * Matrix4.CreateTranslation(_trans[i]) * World[parent];
            }
        }

        public int Bone(string name) => Data.BoneIndex(name);

        // after Pose: turn one bone (and everything under it) by r in model space, about the bone's own position
        public void Turn(int bone, Matrix4 r)
        {
            Vector3 at = World[bone].ExtractTranslation();
            SetWorld(bone, World[bone] * Matrix4.CreateTranslation(-at) * r * Matrix4.CreateTranslation(at));
        }

        // turn a bone about its own position so direction `from` (model space) points along `to`
        void TurnOnto(int bone, Vector3 from, Vector3 to)
        {
            if (from.LengthSquared < 1e-10f || to.LengthSquared < 1e-10f) return;
            from.Normalize();
            to.Normalize();
            Vector3 axis = Vector3.Cross(from, to);
            if (axis.LengthSquared < 1e-12f) return;
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(from, to), -1, 1));
            Turn(bone, Matrix4.CreateFromAxisAngle(axis.Normalized(), angle));
        }

        // put a limb's end back where it was (target: its world before the body moved), two-bone IK in model space: the
        // elbow bends the way it bends now, the end keeps the target's turn (the pirate's blade stays hooked in the wall
        // while his upper body turns to aim)
        public void PinLimb(int shoulder, int elbow, int end, Matrix4 target)
        {
            Vector3 s = World[shoulder].ExtractTranslation(), e = World[elbow].ExtractTranslation();
            Vector3 w = World[end].ExtractTranslation(), t = target.ExtractTranslation();
            float a = (e - s).Length, b = (w - e).Length;
            Vector3 st = t - s;
            float d = st.Length;
            if (d < 1e-5f || a < 1e-5f || b < 1e-5f) return;
            Vector3 dir = st / d;
            float dc = Math.Clamp(d, MathF.Abs(a - b) + 1e-4f, a + b - 1e-4f);
            float along = (a * a - b * b + dc * dc) / (2 * dc);
            float h = MathF.Sqrt(MathF.Max(0, a * a - along * along));
            Vector3 pole = e - s - Vector3.Dot(e - s, dir) * dir;
            if (pole.LengthSquared < 1e-10f) pole = Vector3.Cross(dir, Vector3.UnitY);
            if (pole.LengthSquared < 1e-10f) pole = Vector3.UnitX;
            pole.Normalize();
            Vector3 e2 = s + dir * along + pole * h;
            TurnOnto(shoulder, e - s, e2 - s);
            w = World[end].ExtractTranslation();
            TurnOnto(elbow, w - e2, s + dir * dc - e2);
            Matrix4 m = target;
            m.Row3.Xyz = World[end].ExtractTranslation();
            SetWorld(end, m);
        }

        // after Pose: an upper-body layer (MPH's biped2: shooting, charging, getting hit) over the bones from `root` down,
        // clip a at t (cross-faded from b at tb by 1 - fade while it starts), mixed over the pose already there by weight.
        // The root bone takes the clip's turn in MODEL space (where the legs' pose put it, turned as the clip turns it):
        // the legs' clips lean the pelvis their own way (the pirate's run pitches it forward), and the clip's own spine
        // turn on top of that would tip the upper body over. Below the root, each bone's own local pose
        public void PoseLayer(int root, HdGun.Anim a, float t, HdGun.Anim? b, float tb, float fade, float weight)
        {
            HdGun g = Data;
            bool[] sub = Subtree(root);
            Quaternion turn = ModelTurn(a, t, root);
            if (b != null && fade < 1) turn = Quaternion.Slerp(ModelTurn(b, tb, root), turn, fade);
            Quaternion legs = World[root].ExtractRotation();
            Vector3 at = World[root].ExtractTranslation();
            World[root] = Matrix4.CreateFromQuaternion(Quaternion.Slerp(legs, turn, weight)) * Matrix4.CreateTranslation(at);
            foreach (int i in g.Order)
            {
                if (!sub[i] || i == root) continue;
                g.Sample(a, t, i, out Quaternion r, out Vector3 p);
                if (b != null && fade < 1)
                {
                    g.Sample(b, tb, i, out Quaternion r0, out Vector3 p0);
                    r = Quaternion.Slerp(r0, r, fade);
                    p = Vector3.Lerp(p0, p, fade);
                }
                _rot[i] = Quaternion.Slerp(_rot[i], r, weight);
                _trans[i] = Vector3.Lerp(_trans[i], p, weight);
                Matrix4 local = Matrix4.CreateFromQuaternion(_rot[i]) * Matrix4.CreateTranslation(_trans[i]);
                World[i] = g.Parent[i] >= 0 ? local * World[g.Parent[i]] : local;
            }
        }

        // a bone's turn in model space in clip a at t (its local turns from the top of the skeleton down)
        Quaternion ModelTurn(HdGun.Anim a, float t, int bone)
        {
            HdGun g = Data;
            Matrix4 m = Matrix4.Identity;
            for (int i = bone; i >= 0; i = g.Parent[i])
            {
                g.Sample(a, t, i, out Quaternion r, out _);
                m *= Matrix4.CreateFromQuaternion(r);   // as World: local * parent
            }
            return m.ExtractRotation();
        }

        readonly Dictionary<int, bool[]> _subtrees = new();

        // root and every bone under it
        bool[] Subtree(int root)
        {
            lock (_subtrees)
            {
                if (_subtrees.TryGetValue(root, out bool[]? sub)) return sub;
                HdGun g = Data;
                sub = new bool[g.BoneCount];
                sub[root] = true;
                foreach (int i in g.Order)
                {
                    if (g.Parent[i] >= 0 && sub[g.Parent[i]]) sub[i] = true;
                }
                _subtrees[root] = sub;
                return sub;
            }
        }

        // skin matrices for one material's palette, row-vector (OpenTK) layout, 16 floats each -- uploaded untransposed
        public void FillPalette(int material, float[] dst)
        {
            int[] pal = Data.Materials[material].Palette;
            for (int k = 0; k < pal.Length; k++)
            {
                Matrix4 m = Data.InvBind[pal[k]] * World[pal[k]];
                int o = k * 16;
                dst[o] = m.M11; dst[o + 1] = m.M12; dst[o + 2] = m.M13; dst[o + 3] = m.M14;
                dst[o + 4] = m.M21; dst[o + 5] = m.M22; dst[o + 6] = m.M23; dst[o + 7] = m.M24;
                dst[o + 8] = m.M31; dst[o + 9] = m.M32; dst[o + 10] = m.M33; dst[o + 11] = m.M34;
                dst[o + 12] = m.M41; dst[o + 13] = m.M42; dst[o + 14] = m.M43; dst[o + 15] = m.M44;
            }
        }

        public float Scale => PlayerHeight / Height;

        // model -> world: the bind feet on y = 0, scaled to MPH's player height, turned to `facing` (its horizontal part;
        // model +Z = forward), standing where MphRead stands a player at `position` (HunterRig.Placement's feet: position
        // + MinPickupHeight for a scale-1 hunter)
        public Matrix4 ModelMatrix(Hunter hunter, Vector3 position, Vector3 facing, float lift = 0)
        {
            var lateral = new Vector3(facing.X, 0, facing.Z);
            lateral = lateral.LengthSquared > 1e-12f ? lateral.Normalized() : Vector3.UnitZ;
            Vector3 side = Vector3.Cross(Vector3.UnitY, lateral);
            var turn = Matrix4.Identity;
            turn.Row0.Xyz = side;
            turn.Row1.Xyz = Vector3.UnitY;
            turn.Row2.Xyz = lateral;
            float bottom = Fixed.ToFloat(Metadata.PlayerValues[(int)hunter].MinPickupHeight);
            return Matrix4.CreateTranslation(0, -Feet, 0) * Matrix4.CreateScale(DrawScale) * turn
                * Matrix4.CreateTranslation(position.X, position.Y + bottom + lift, position.Z);
        }
    }

    // One seat's clip clock on a native body: MphRead's biped state (the legs layer's PlayerAnimation, the Trooper's
    // wall cling) -> one of the body's own clips (BodyClips), its own time, and a short cross-fade from the clip before.
    // Walking clips play at the seat's ground speed over the clip's own (body.cfg "speed"), so the feet don't slide.
    public sealed class NativeBodyAnimator
    {
        readonly NativeBody _body;
        readonly BodyClips _map;
        HdGun.Anim? _cur, _prev;
        float _t, _prevT, _fade;    // _fade: how far into the cross-fade from _prev (1 = done)
        float _fadeTime = FadeTime; // that cross-fade's length (BodyClips.FadeOut of the clip it leaves)
        string _key = "";
        bool _clung;
        float _outLeft;             // the wall hang's let-go clip still playing (seconds)
        Vector3 _wallInto;          // into the wall he clings to (minus its normal)
        Vector3? _lastPos;
        // the upper-body layer (MPH's biped2 states the map has a torso clip for): its clip, time, the clip it fades from,
        // and its weight over the legs (fades in when the state starts, out when the torso follows the legs again)
        readonly int _torsoRoot;
        // the aim twist (MPH's spine tilt, PlayerDraw: the biped's Spine_1 pitched toward the aim) and, on the wall, its turn
        // toward the aim: the wall hang pins the body facing out, so the upper body turns instead (_wallTwist fades it)
        readonly int _aimBone;
        readonly int _pinShoulder, _pinElbow, _pinEnd;
        float _wallTwist;
        HdGun.Anim? _tcur, _tprev;
        float _tt, _tprevT, _tfade, _tw;
        bool _tloop;
        PlayerAnimation _tstate = PlayerAnimation.None;
        int _tframe;
        public const float FadeTime = 0.12f;
        // BodyClips.Lean: its 4 clips (forward, back, left, right) + the idle clip, the move direction smoothed (x = right,
        // y = forward), their weights, whether the legs' clip (and the one it fades from) is that blend, the pose mix's scratch
        readonly HdGun.Anim[]? _lean;
        readonly HdGun.Anim? _leanIdle;
        Vector2 _leanDir = Vector2.UnitY;
        readonly float[] _leanW = new float[5];
        bool _curLean, _prevLean;
        readonly HdGun.Anim[] _mixA = new HdGun.Anim[10];
        readonly float[] _mixT = new float[10], _mixW = new float[10];
        const string LeanKey = "lean";
        // BodyClips.FlourishSet: the flourish clip playing (null = none), the last one played (for its weight while it
        // fades out), how long its hold-release eases (seconds)
        string? _flourish, _lastFlourish;
        int _playNow = BodyFlourish.PlayNow;
        const float FlourishEase = 0.25f, FlourishFade = 0.3f;
        const float LeanTime = 0.1f;    // the direction's smoothing (seconds)

        public NativeBodyAnimator(NativeBody body, BodyClips map)
        {
            _body = body;
            _map = map;
            _torsoRoot = map.TorsoRoot.Length > 0 ? body.Bone(map.TorsoRoot) : -1;
            _aimBone = map.AimBone.Length > 0 ? body.Bone(map.AimBone) : -1;
            (_pinShoulder, _pinElbow, _pinEnd) = PinBones(body, map);
            if (map.Lean is (string f, string b, string l, string r)
                && body.Clip(f) is HdGun.Anim fa && body.Clip(b) is HdGun.Anim ba && body.Clip(l) is HdGun.Anim la && body.Clip(r) is HdGun.Anim ra)
            {
                _lean = new[] { fa, ba, la, ra };
                _leanIdle = body.Clip(map.Idle);
            }
        }

        public NativeBody Body => _body;
        public string Playing => _cur?.Name ?? "";
        public string PlayingTorso => _tw > 0 ? _tcur?.Name ?? "" : "";
        public float Time => _t;

        // while a wall clip plays (the grab, the hang, the let-go): the direction the body's +Z must point, into the wall.
        // The pirate's wall clips have the wall on his +Z side (feet at model z ~1.0, the blade hooked in above, head
        // and gun toward -Z: measured with -bodytest --bones), so he hangs with his back to it, facing out
        public Vector3? WallFacing => _clung || _outLeft > 0 ? _wallInto : null;

        // a player's step of dt seconds (its position: for the ground speed)
        public void Step(PlayerEntity p, Vector3 position, float dt)
        {
            float speed = 0;
            Vector2 move = Vector2.Zero;
            if (_lastPos is Vector3 last && dt > 0)
            {
                move = new Vector2(position.X - last.X, position.Z - last.Z) / dt;
                speed = move.Length;
            }
            _lastPos = position;
            BodyClips.Choice c;
            bool snap = false;
            if (p.WallClinging && p.WallClingNormal.LengthSquared > 0.5f)
            {
                c = new BodyClips.Choice("wall", _map.WallIn, false, Then: _map.WallLoop);
                _clung = true;
                _wallInto = new Vector3(-p.WallClingNormal.X, 0, -p.WallClingNormal.Z);
            }
            else
            {
                if (_clung)
                {
                    // let go: the let-go clip turns him back to face the wall, so it plays only while he still looks at
                    // it (a fall); a jump away cuts straight to the air clip -- the hang already faces out, and a
                    // cross-fade across the turned frame would swing the body round
                    _clung = false;
                    bool facingWall = p.Field70 * _wallInto.X + p.Field74 * _wallInto.Z > 0;
                    _outLeft = facingWall ? _body.Clip(_map.WallOut)?.Duration ?? 0 : 0;
                    snap = _outLeft <= 0;
                }
                if (_outLeft > 0)
                {
                    _outLeft -= dt;
                    c = new BodyClips.Choice("wallout", _map.WallOut, false);
                }
                else c = Flourish(p, _map.Choose(p) with { Snap = snap });
                if (_lean != null) c = Lean(p, c, move, speed, dt);
                AnimationInfo legs = p.HostBipedModel1.AnimInfo;
                if (_map.Fit.Contains((PlayerAnimation)legs.Index[0]) && legs.FrameCount[0] > 0 && _body.Clip(c.Clip) is HdGun.Anim fit)
                {
                    c = c with { Rate = fit.Duration * 30f / legs.FrameCount[0] }; // MPH's clips run at 30 frames a second
                }
            }
            if (c.Walk && _body.Speeds.TryGetValue(c.Clip, out float own) && own > 0)
            {
                c = c with { Rate = Math.Clamp(speed / (own * _body.DrawScale), 0.5f, 2f) };
            }
            Step(c, dt);
            // the wall clips own the whole body
            bool hanging = _clung && _key.Contains('>');
            _wallTwist = hanging ? Math.Min(1, _wallTwist + dt / FadeTime) : Math.Max(0, _wallTwist - dt / FadeTime);
            if (_clung || _outLeft > 0) StepTorso(PlayerAnimation.None, 0, dt, cut: true);
            else
            {
                AnimationInfo info = p.BipedModel2.AnimInfo;
                StepTorso((PlayerAnimation)info.Index[0], info.Frame[0], dt);
            }
        }

        // BodyClips.FlourishSet: MPH's Flourish state plays the body's own flourish at its own speed, then the idle loop.
        // MPH's Flourish is its hunter's clip length, so when MphRead is back in Idle first the flourish still plays out;
        // anything else (a step, a shot) cuts it with the usual cross-fade
        BodyClips.Choice Flourish(PlayerEntity p, BodyClips.Choice c)
        {
            if (_map.FlourishSet == null) return c;
            var state = (PlayerAnimation)p.HostBipedModel1.AnimInfo.Index[0];
            // standing still: the map picked its idle loop (MPH's legs can sit at None there, as in the solo arena)
            bool idle = state == PlayerAnimation.Idle || c.Clip == _map.ClipFor(PlayerAnimation.Idle);
            if (idle && _playNow != BodyFlourish.PlayNow)
            {
                _playNow = BodyFlourish.PlayNow;
                _flourish = BodyFlourish.Start(_body, _map.FlourishSet);
            }
            else if (state == PlayerAnimation.Flourish)
            {
                _flourish ??= BodyFlourish.Start(_body, _map.FlourishSet);
            }
            else if (!idle || _flourish == null || _cur?.Name != _flourish || _t >= _cur.Duration)
            {
                _flourish = null;
            }
            if (_flourish == null) return c;
            _lastFlourish = _flourish;
            return new BodyClips.Choice(_flourish, _flourish, false, Then: _map.ClipFor(PlayerAnimation.Idle));
        }

        // how far the flourish has the body now (BodyClips.FlourishWeight): eased in from its start and out to its end,
        // and out with the cross-fade when something cuts it
        float FlourishWeight()
        {
            if (_lastFlourish == null) return 0;
            float W(HdGun.Anim? a, float t) => a?.Name == _lastFlourish
                ? Math.Clamp(Math.Min(t, a.Duration - t) / FlourishEase, 0, 1) : 0;
            if (_prev != null && _fade < 1) return W(_cur, _t) * _fade + W(_prev, _prevT) * (1 - _fade);
            return W(_cur, _t);
        }

        // BodyClips.Lean: any of its 4 clips is one state ("lean") whose pose blends them by the seat's move direction
        // against its facing (MphRead's own sides: forward = (Field70, Field74), right = (-Field74, Field70), as
        // PlayerInput's MoveRightLeft moves it), smoothed: turning the stick sweeps through the leans instead of snapping
        // between them, and a reversal passes through the idle pose (the smoothed direction is short there)
        BodyClips.Choice Lean(PlayerEntity p, BodyClips.Choice c, Vector2 move, float speed, float dt)
        {
            int k = Array.FindIndex(_lean!, a => a.Name == c.Clip);
            if (k < 0) return c;
            Vector2 want = k switch { 0 => Vector2.UnitY, 1 => -Vector2.UnitY, 2 => -Vector2.UnitX, _ => Vector2.UnitX };
            if (speed > 0.05f)
            {
                var forward = new Vector2(p.Field70, p.Field74);
                var right = new Vector2(-p.Field74, p.Field70);
                want = new Vector2(Vector2.Dot(move, right), Vector2.Dot(move, forward)) / speed;
            }
            _leanDir = _curLean ? Vector2.Lerp(_leanDir, want, 1 - MathF.Exp(-dt / LeanTime)) : want;
            float x = _leanDir.X, y = _leanDir.Y, len = _leanDir.Length;
            float sum = Math.Abs(x) + Math.Abs(y);
            float m = sum > 1e-4f ? Math.Min(1, len) / sum : 0;
            _leanW[0] = Math.Max(0, y) * m;
            _leanW[1] = Math.Max(0, -y) * m;
            _leanW[2] = Math.Max(0, -x) * m;
            _leanW[3] = Math.Max(0, x) * m;
            _leanW[4] = 1 - (_leanW[0] + _leanW[1] + _leanW[2] + _leanW[3]);
            if (_leanIdle == null && _leanW[4] > 0.999f)
            {
                Array.Clear(_leanW);
                _leanW[k] = 1;
            }
            return c with { Key = LeanKey, Clip = _lean![0].Name };
        }

        // a body with no player of its own (the Trooper's standing body while his Scout Drone flies)
        public void StepStanding(float dt)
        {
            Step(_map.Standing(), dt);
            StepTorso(PlayerAnimation.None, 0, dt);
        }

        // the torso layer's step: biped2's state + frame (a lower frame in the same state = MphRead restarted it: the next
        // shot or hit); cut: off at once
        void StepTorso(PlayerAnimation state, int frame, float dt, bool cut = false)
        {
            HdGun.Anim? clip = null;
            bool loop = false;
            if (_torsoRoot >= 0 && !cut && _map.TorsoFor(state, out (string Clip, bool Loop) e))
            {
                clip = _body.Clip(e.Clip);
                loop = e.Loop;
            }
            if (clip == null)
            {
                _tstate = PlayerAnimation.None;
                _tw = cut ? 0 : Math.Max(0, _tw - dt / FadeTime);
                if (_tw <= 0) _tcur = _tprev = null;
                else AdvanceTorso(dt);
                return;
            }
            bool restart = state == _tstate && frame < _tframe && !loop;
            if (clip != _tcur || restart || _tw <= 0)
            {
                // a new torso clip: cross-faded from the one still showing, if any
                _tprev = _tw > 0 ? _tcur : null;
                _tprevT = _tt;
                _tfade = _tprev == null ? 1 : 0;
                _tcur = clip;
                _tt = 0;
            }
            _tloop = loop;
            _tstate = state;
            _tframe = frame;
            _tw = Math.Min(1, _tw + dt / FadeTime);
            AdvanceTorso(dt);
        }

        void AdvanceTorso(float dt)
        {
            if (_tcur == null) return;
            _tt += dt;
            if (_tloop && _tcur.Duration > 0) _tt %= _tcur.Duration;
            else if (_tt > _tcur.Duration) _tt = _tcur.Duration;
            if (_tprev != null && _tfade < 1) _tfade = Math.Min(1, _tfade + dt / FadeTime);
        }

        // a step of dt seconds in state c: a new key starts its clip from the beginning, cross-faded from the old one
        // (the pose itself is Pose's, at draw time: one NativeBody is shared by every seat wearing it)
        public void Step(BodyClips.Choice c, float dt)
        {
            if (c.Key != _key && !_key.StartsWith(c.Key + ">", StringComparison.Ordinal) || _cur == null)
            {
                HdGun.Anim? next = _body.Clip(c.Clip) ?? _body.Clip(_map.Idle);
                if (next != null && next != _cur)
                {
                    _prev = _cur;
                    _prevT = _t;
                    _prevLean = _curLean;
                    _fade = _prev == null || c.Snap ? 1 : 0;
                    _fadeTime = c.Key == _flourish ? Math.Max(FadeOf(_prev), FlourishFade) : FadeOf(_prev);
                    _cur = next;
                    _t = 0;
                }
                _key = c.Key;
                _curLean = c.Key == LeanKey;
            }
            if (_cur == null) return;
            _t += dt * c.Rate;
            if (_key.Contains('>'))
            {
                // the follow-up loop of a one-shot (the wall hang after the grab)
                if (_cur.Duration > 0) _t %= _cur.Duration;
            }
            else if (c.Loop && _cur.Duration > 0) _t %= _cur.Duration;
            else if (_t > _cur.Duration)
            {
                // a one-shot that ends: its follow-up loop (into the wall hang -> the hang), else it holds its last frame
                if (c.Then != null && _body.Clip(c.Then) is HdGun.Anim then)
                {
                    _prev = _cur;
                    _prevT = _cur.Duration;
                    _prevLean = _curLean;
                    _fade = 0;
                    _fadeTime = FadeOf(_prev);
                    _cur = then;
                    _t = 0;
                    _key = c.Key + ">" + c.Then;
                }
                else _t = _cur.Duration;
            }
            if (_prev != null && _fade < 1) _fade = Math.Min(1, _fade + dt / _fadeTime);
        }

        float FadeOf(HdGun.Anim? from) => from != null && _map.FadeOut.TryGetValue(from.Name, out float f) && f > 0 ? f
            : from != null && from.Name == _lastFlourish ? FlourishFade : FadeTime;

        // the body in this seat's pose now (its World / FillPalette until the next Pose); who: the player it draws (null for
        // a standing body), for the map's AfterPose
        public bool Pose(PlayerEntity? who = null)
        {
            if (_cur == null) return false;
            if (_lean != null && (_curLean || _prevLean && _prev != null && _fade < 1)) PoseLean();
            else if (_prev != null && _fade < 1) _body.Pose(_prev, _prevT, _cur, _t, _fade);
            else _body.Pose(_cur, _t);
            if (_torsoRoot >= 0 && _tcur != null && _tw > 0) _body.PoseLayer(_torsoRoot, _tcur, _tt, _tprev, _tprevT, _tfade, _tw);
            if (who != null && _aimBone >= 0) AimTwist(who);
            _map.FlourishWeight = FlourishWeight();
            _map.AfterPose?.Invoke(_body, who);
            return true;
        }

        // the legs' pose with BodyClips.Lean's blend in it (now, or the clip it cross-fades from)
        void PoseLean()
        {
            int n = 0;
            float w = _prev != null && _fade < 1 ? _fade : 1;
            AddState(_curLean, _cur!, _t, w, ref n);
            if (w < 1) AddState(_prevLean, _prev!, _prevT, 1 - w, ref n);
            _body.PoseMix(_mixA, _mixT, _mixW, n);
        }

        void AddState(bool lean, HdGun.Anim clip, float t, float w, ref int n)
        {
            if (!lean)
            {
                Add(clip, t, w, ref n);
                return;
            }
            // the lean clips and the idle loop all loop
            for (int k = 0; k < 4; k++) Add(_lean![k], _lean[k].Duration > 0 ? t % _lean[k].Duration : 0, _leanW[k] * w, ref n);
            if (_leanIdle != null) Add(_leanIdle, _leanIdle.Duration > 0 ? t % _leanIdle.Duration : 0, _leanW[4] * w, ref n);
        }

        void Add(HdGun.Anim a, float t, float w, ref int n)
        {
            if (w <= 0) return;
            _mixA[n] = a;
            _mixT[n] = t;
            _mixW[n] = w;
            n++;
        }

        // MPH's spine tilt (PlayerDraw: Spine_1 pitched by the aim's height, |sin| capped at 2896/4096), in model space about
        // the bone; on the wall the upper body also turns left/right from facing out toward the aim (the legs stay hung)
        void AimTwist(PlayerEntity p)
        {
            Vector3 aim = p.FacingVector;
            if (aim.LengthSquared < 1e-6f) return;
            aim.Normalize();
            // the draw facing (DrawBody's): model +Z, into the wall while a wall clip plays
            Vector3 lateral = WallFacing ?? new Vector3(p.Field70, 0, p.Field74);
            lateral.Y = 0;
            if (lateral.LengthSquared < 1e-6f) return;
            lateral.Normalize();
            Vector3 side = Vector3.Cross(Vector3.UnitY, lateral);
            var d = new Vector3(Vector3.Dot(aim, side), aim.Y, Vector3.Dot(aim, lateral)); // the aim in model space
            AimAndPin(_body, _map, d, WallFacing != null, _wallTwist, _aimBone, (_pinShoulder, _pinElbow, _pinEnd));
        }

        // the aim twist on a posed body, and on the wall the hooked limb put back where the wall clip had it
        public static void AimAndPin(NativeBody body, BodyClips map, Vector3 d, bool onWall, float wall, int aimBone, (int S, int E, int W) pin)
        {
            bool pinned = onWall && pin.S >= 0 && pin.E >= 0 && pin.W >= 0;
            Matrix4 hook = pinned ? body.World[pin.W] : default;
            body.Turn(aimBone, AimTurn(d, onWall, wall, map));
            if (pinned) body.PinLimb(pin.S, pin.E, pin.W, hook);
        }

        public static (int, int, int) PinBones(NativeBody body, BodyClips map)
            => map.WallPin is (string ps, string pe, string pw) ? (body.Bone(ps), body.Bone(pe), body.Bone(pw)) : (-1, -1, -1);

        // the aim twist for an aim d in model space: pitch toward d.Y (|sin| capped as MPH caps it), and on the wall (where
        // the upper body faces out, model -Z) a left/right turn toward d too, capped, by weight wall
        public static Matrix4 AimTurn(Vector3 d, bool onWall, float wall, BodyClips map)
        {
            float heading0 = onWall ? MathF.PI : 0;
            float yaw = 0;
            if (onWall && wall > 0 && d.X * d.X + d.Z * d.Z > 1e-6f)
            {
                float dh = MathF.Atan2(d.X, d.Z) - heading0;
                dh = MathF.Atan2(MathF.Sin(dh), MathF.Cos(dh));
                yaw = Math.Clamp(dh, -map.WallYawLimit, map.WallYawLimit) * wall;
            }
            float len = d.Length;
            float sin = Math.Clamp(len > 0 ? d.Y / len : 0, -map.AimPitchSin, map.AimPitchSin);
            float pitch = MathF.Atan2(sin, MathF.Sqrt(1 - sin * sin));
            float heading = heading0 + yaw;
            var forward = new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading));
            Vector3 axis = Vector3.Cross(forward, Vector3.UnitY);   // a turn about it lifts forward toward +Y
            return Matrix4.CreateRotationY(yaw) * Matrix4.CreateFromAxisAngle(axis, pitch);
        }
    }

    // Which of a body's own clips plays for each MphRead biped state. Picked by the clip names the body has: the Prime 1
    // Space Pirate's set (the troopers, D22).
    public sealed class BodyClips
    {
        // Key: the state (a new key restarts); Walk: played at the ground speed; Then: a one-shot's follow-up loop;
        // Snap: starts without the cross-fade
        public readonly record struct Choice(string Key, string Clip, bool Loop, float Rate = 1, bool Walk = false, string? Then = null,
            bool Snap = false);

        public string Idle = "";
        readonly Dictionary<PlayerAnimation, (string Clip, bool Loop)> _legs = new();
        public string WallIn = "", WallLoop = "", WallOut = "";
        // drawn this far (world units) above where MphRead stands the player (Dark Samus's hover, D8)
        public float Lift;
        // the aim twist's bone (MPH turns the biped's Spine_1; "" = none), MPH's pitch limit (sin 2896/4096: 45 degrees) and
        // how far the upper body turns left/right toward the aim while the body hangs on a wall
        public string AimBone = "";
        public float AimPitchSin = 2896 / 4096f;
        public float WallYawLimit = MathHelper.DegreesToRadians(75);
        // the limb that holds the wall in the wall clips (shoulder, elbow, end): kept where the clip has it while the upper
        // body turns to aim
        public (string, string, string)? WallPin;
        // called after every pose at draw time, before skinning: bone overrides on the posed World (NativeBody.SetWorld);
        // the player is null for a standing body
        public Action<NativeBody, PlayerEntity?>? AfterPose;
        // legs states drawn with the seat's HD suit on MPH's own rig and clips when the skin has one (Dark Samus's morph,
        // unmorph and jumps: owner 2026-10-08), and the morph ball trail's colour (null = MPH's)
        public readonly HashSet<PlayerAnimation> SuitLegs = new();
        public Vector3? Trail;
        // the legs' 4 directional clips (forward, back, left, right) blended by the move direction instead of one per walk
        // state (NativeBodyAnimator.Lean: Dark Samus's glide/float leans, owner 2026-10-08 "she kind of snaps to different
        // positions" circling the stick); null = one clip per state
        public (string F, string B, string L, string R)? Lean;
        // legs states whose one-shot clip is stretched to MphRead's own length of the state, so it ends as the state does
        // (Dark Samus's own unmorph, owner 2026-10-08), and per clip how long the cross-fade out of it takes when that
        // isn't NativeBodyAnimator.FadeTime (her uncurl ends crouched: standing up into ready takes longer)
        public readonly HashSet<PlayerAnimation> Fit = new();
        public readonly Dictionary<string, float> FadeOut = new();
        // MPH's idle Flourish: the set its clip comes from (BodyFlourish, owner 2026-10-15; null = the Flourish state's own
        // clip), and while it plays, how far it has the body (0..1, eased in and out: AfterPose holds let go of it)
        public BodyFlourish.Set? FlourishSet;
        public float FlourishWeight;

        // a legs-layer state's clip
        public void Set(PlayerAnimation a, string clip, bool loop) => _legs[a] = (clip, loop);

        // the upper-body layer: the bone it starts at (MPH splits biped1/biped2 at the spine) and its clip for each biped2
        // state (shooting, charging, hits: MphRead plays those on biped2 only). States with no torso clip follow the legs
        public string TorsoRoot = "";
        readonly Dictionary<PlayerAnimation, (string Clip, bool Loop)> _torso = new();

        public void SetTorso(PlayerAnimation a, string clip, bool loop) => _torso[a] = (clip, loop);

        public bool TorsoFor(PlayerAnimation a, out (string Clip, bool Loop) e) => _torso.TryGetValue(a, out e);

        // other characters' maps (each returns null for a body that isn't theirs), asked before the pirate's
        static readonly List<Func<NativeBody, BodyClips?>> _registered = new();

        public static void Register(Func<NativeBody, BodyClips?> map)
        {
            lock (_registered) _registered.Add(map);
        }

        public static BodyClips? For(NativeBody body)
        {
            lock (_registered)
            {
                foreach (Func<NativeBody, BodyClips?> f in _registered)
                {
                    if (f(body) is BodyClips m) return m;
                }
            }
            if (body.Clip("B_ready_pirate") != null)
            {
                BodyFlourish.Prepare(body, BodyFlourish.Trooper, "B_ready_pirate");
                return Pirate();
            }
            return null;
        }

        static BodyClips Pirate()
        {
            var m = new BodyClips { Idle = "B_ready_pirate", WallIn = "B_intowallhang_pirate", WallLoop = "B_wallhang_pirate", WallOut = "B_outofwallhang_pirate",
                FlourishSet = BodyFlourish.Trooper };
            void L(PlayerAnimation a, string clip, bool loop) => m._legs[a] = (clip, loop);
            foreach (PlayerAnimation a in new[] { PlayerAnimation.Idle, PlayerAnimation.Spawn, PlayerAnimation.Flourish, PlayerAnimation.Turn,
                PlayerAnimation.Charge, PlayerAnimation.Shoot, PlayerAnimation.ChargeShoot, PlayerAnimation.Unmorph, PlayerAnimation.Morph })
            {
                L(a, "B_ready_pirate", true);
            }
            L(PlayerAnimation.WalkForward, "B_runloop_pirate", true);
            L(PlayerAnimation.WalkBackward, "B_backward_pirate", true);
            L(PlayerAnimation.WalkLeft, "B_strafleft_pirate", true);
            L(PlayerAnimation.WalkRight, "B_strafright_pirate", true);
            foreach (PlayerAnimation j in new[] { PlayerAnimation.JumpNeutral, PlayerAnimation.JumpBack, PlayerAnimation.JumpForward,
                PlayerAnimation.JumpLeft, PlayerAnimation.JumpRight })
            {
                L(j, "B_jumpairloop_pirate", true);
            }
            foreach (PlayerAnimation l in new[] { PlayerAnimation.LandNeutral, PlayerAnimation.LandLeft, PlayerAnimation.LandRight })
            {
                L(l, "B_outofjump_pirate", false);
            }
            L(PlayerAnimation.DamageFront, "R_lightf_pirate", false);
            L(PlayerAnimation.DamageBack, "R_lightb_pirate", false);
            L(PlayerAnimation.DamageLeft, "R_lightl_pirate", false);
            L(PlayerAnimation.DamageRight, "R_lightr_pirate", false);
            // upper body over the legs: gun up and firing, the charge held, a flinch when hit
            m.TorsoRoot = "Spine_1";
            m.AimBone = "Spine_1";
            m.WallPin = ("L_shoulder", "L_elbow", "L_wrist");   // the blade arm, hooked in above him
            m.SetTorso(PlayerAnimation.Shoot, "A_standshot_pirate", true);
            m.SetTorso(PlayerAnimation.ChargeShoot, "A_standshot_pirate", true);
            m.SetTorso(PlayerAnimation.Charge, "A_gunloop_pirate", true);
            m.SetTorso(PlayerAnimation.DamageFront, "R_flinch1_pirate", false);
            m.SetTorso(PlayerAnimation.DamageBack, "R_flinch2_pirate", false);
            m.SetTorso(PlayerAnimation.DamageLeft, "R_flinch1_pirate", false);
            m.SetTorso(PlayerAnimation.DamageRight, "R_flinch2_pirate", false);
            return m;
        }

        // the clip a legs-layer state plays
        public string ClipFor(PlayerAnimation a) => _legs.TryGetValue(a, out (string Clip, bool Loop) e) ? e.Clip : Idle;

        // the legs layer's clip now (one key per clip: idle, turn and shoot share the ready loop, so it keeps playing)
        public Choice Choose(PlayerEntity p)
        {
            var a = (PlayerAnimation)p.HostBipedModel1.AnimInfo.Index[0];
            if (!_legs.TryGetValue(a, out (string Clip, bool Loop) e)) e = (Idle, true);
            bool walk = a is PlayerAnimation.WalkForward or PlayerAnimation.WalkBackward or PlayerAnimation.WalkLeft or PlayerAnimation.WalkRight;
            return new Choice(e.Clip, e.Clip, e.Loop, Walk: walk);
        }

        // the Trooper's standing body while his Scout Drone flies
        public Choice Standing() => new(Idle, Idle, true);
    }
}
