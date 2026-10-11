using System;
using System.Collections.Generic;
using System.Globalization;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // A first-person arm cannon cut from a native body (owner 2026-10-08: the Troopers' and Dark Samus's own cannons in
    // place of MPH's first-person gun, every material effect the body shows kept): the body's triangles from the elbow
    // down (every vertex weighted to R_elbow or a bone under it, so the elbow ring stays whole), drawn by the body's own
    // materials (App CampaignRetroBody: same programs, textures, scrolls) with the body held in one pose of its own.
    // Cannon space is MPH's gun space: X = cross(Y, Z) (the player's left), Y up, Z forward along the barrel, the muzzle
    // locator at the origin -- so the cannon's muzzle lands on MPH's own muzzle point, where the sim spawns the shots and
    // MphRead draws the muzzle flash, charge glow and gun smoke.
    public sealed class BodyArmCannon
    {
        public readonly NativeBody Body;
        public readonly int Elbow, Tip;
        // per material: the body's vertex indices the cut keeps (whole triangles, 3 per triangle, in the body's order)
        public readonly int[][] Cut;
        // body model space (in the cut pose) -> cannon space; the cut's length behind the muzzle (model units)
        public readonly Matrix4 ToCannon;
        public readonly float Length;
        // first-person size, times the body's own scale (by eye against MPH's gun, 2026-10-08: at the body's own size
        // the Dark Samus cannons fill a third of the screen, the Trooper's forearm sits behind the camera)
        public readonly float Size;
        // where MPH's first-person charge glow, muzzle flash and gun smoke go (owner 2026-10-08 23:11: the Trooper's under
        // the spike, by the hand): cannon space, world units at the first-person size; null = on the muzzle. Drawing only
        // (PlayerEntity.HostFxMuzzle): the shots still leave MPH's muzzle point
        public readonly Vector3? Blast;
        // the cannon's light, times the body's (owner 2026-10-08 23:11: Echoes Dark Samus's a little lighter)
        public readonly float Light;
        readonly Spec _spec;
        readonly HdGun.Anim? _poseClip, _handClip;
        readonly bool[]? _hand;

        // a body's cannon: the pose it's cut in (clip at t) and its muzzle locator; Hand/HandClip: the bones from Hand down
        // play HandClip (looped) over the cut pose -- the Trooper's claw as it moves when he stands ready
        sealed record Spec(string? Clip, float T, string Tip, float Size, Vector3? Blast = null, float Light = 1, string? Hand = null,
            string? HandClip = null);

        BodyArmCannon(NativeBody body, int elbow, int tip, int[][] cut, Matrix4 toCannon, float length, Spec spec)
        {
            Body = body;
            Elbow = elbow;
            Tip = tip;
            Cut = cut;
            ToCannon = toCannon;
            Length = length;
            Size = spec.Size;
            Blast = spec.Blast;
            Light = spec.Light;
            _spec = spec;
            _poseClip = spec.Clip != null ? body.Clip(spec.Clip) : null;
            _handClip = spec.HandClip != null ? body.Clip(spec.HandClip) : null;
            int hand = spec.Hand != null ? body.Bone(spec.Hand) : -1;
            if (_handClip != null && hand >= 0)
            {
                HdGun g = body.Data;
                _hand = new bool[g.BoneCount];
                foreach (int i in g.Order) _hand[i] = i == hand || g.Parent[i] >= 0 && _hand[g.Parent[i]];
            }
        }

        // the shared body in the cut pose (its World / FillPalette until the next Pose); `seconds`: the hand's clip time
        public void Pose(double seconds = 0)
        {
            HdGun g = Body.Data;
            if (_poseClip != null) Body.Pose(_poseClip, _spec.T);
            else
            {
                for (int i = 0; i < g.BoneCount; i++) Body.World[i] = Matrix4.CreateTranslation(g.Bind[i]);
            }
            if (_hand == null || _handClip == null || _handClip.Duration <= 0) return;
            float t = (float)(seconds % _handClip.Duration);
            foreach (int i in g.Order)
            {
                if (!_hand[i]) continue;
                g.Sample(_handClip, t, i, out Quaternion r, out Vector3 p);
                Body.World[i] = Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(p) * Body.World[g.Parent[i]];
            }
        }

        // the pose each body is cut in (a frame of its own where the forearm is held as when it fires) and its muzzle:
        // the Troopers (Prime 1 pirate rig) R_gun_LCTR; Dark Samus GUN_Particle_LCTR (Echoes) / Gun_LCTR (Corruption), in
        // the frames her D14 hold comes from (DarkSamusBody.HoldOf)
        static Spec? SpecOf(NativeBody body)
        {
            if (body.Bone("R_gun_LCTR") >= 0)
            {
                return new Spec(body.Clip("A_gunloop_pirate") != null ? "A_gunloop_pirate" : null, 0, "R_gun_LCTR", 0.7f,
                    Blast: TrooperBlast); // owner 10-08: no idle claw (was Hand: "R_wrist", HandClip: "B_ready_pirate")
            }
            if (body.Clip("A_MissileBarrage_dark_samus") != null)
                return new Spec("A_MissileBarrage_dark_samus", 0, DarkSamusBody.TipOf(body), 0.7f, Light: EchoesLight);
            if (body.Clip("a_mega_blaster_c_steady_dark_samus") != null)
                return new Spec("a_mega_blaster_c_steady_dark_samus", 0, DarkSamusBody.TipOf(body), 0.75f);
            return null;
        }

        // by eye on the desktop, 2026-10-08 (pictures in handoffs/first-person-arm-cannons_2026-10-08_pictures)
        static readonly Vector3 TrooperBlast = new(-0.015f, -0.055f, -0.045f);
        const float EchoesLight = 1.6f;

        // null for a body with no known arm cannon. Poses the shared body: call before any seat animates it (at load)
        public static BodyArmCannon? For(NativeBody body)
        {
            if (SpecOf(body) is not Spec spec) return null;
            int elbow = body.Bone("R_elbow"), tip = body.Bone(spec.Tip);
            if (elbow < 0 || tip < 0) return null;
            HdGun g = body.Data;
            var sub = new bool[g.BoneCount];
            foreach (int i in g.Order) sub[i] = i == elbow || g.Parent[i] >= 0 && sub[g.Parent[i]];
            var cut = new int[g.Materials.Length][];
            for (int m = 0; m < g.Materials.Length; m++)
            {
                HdGun.Material mat = g.Materials[m];
                var keep = new List<int>();
                bool On(int v)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    for (int k = 0; k < 4; k++)
                    {
                        if (mat.Vertices[o + 15 + k] > 0 && sub[mat.Palette[(int)mat.Vertices[o + 11 + k]]]) return true;
                    }
                    return false;
                }
                for (int v = 0; v + 2 < mat.VertexCount; v += 3)
                {
                    if (On(v) && On(v + 1) && On(v + 2))
                    {
                        keep.Add(v);
                        keep.Add(v + 1);
                        keep.Add(v + 2);
                    }
                }
                cut[m] = keep.ToArray();
            }
            var c = new BodyArmCannon(body, elbow, tip, cut, Matrix4.Identity, 0, spec);
            c.Pose();
            // the barrel: elbow -> muzzle; its top: the model's up, square to the barrel
            Vector3 e = body.World[elbow].Row3.Xyz, k0 = body.World[tip].Row3.Xyz;
            Vector3 f = (k0 - e).Normalized();
            Vector3 up = Vector3.UnitY - Vector3.Dot(Vector3.UnitY, f) * f;
            up = up.LengthSquared > 1e-8f ? up.Normalized() : Vector3.UnitZ;
            Vector3 left = Vector3.Cross(up, f);
            // row vectors: v * M = (v.left, v.up, v.f) after the muzzle goes to the origin
            var rot = new Matrix4(left.X, up.X, f.X, 0, left.Y, up.Y, f.Y, 0, left.Z, up.Z, f.Z, 0, 0, 0, 0, 1);
            Matrix4 to = Matrix4.CreateTranslation(-k0) * rot;
            // the cut's reach behind the muzzle
            float back = 0;
            var pal = new float[32 * 16];
            for (int m = 0; m < g.Materials.Length; m++)
            {
                if (cut[m].Length == 0) continue;
                body.FillPalette(m, pal);
                foreach (int v in cut[m]) back = MathF.Max(back, -Vector3.TransformPosition(Skinned(g.Materials[m], v, pal), to).Z);
            }
            return new BodyArmCannon(body, elbow, tip, cut, to, back, spec);
        }

        // one vertex skinned by a filled palette (model space)
        static Vector3 Skinned(HdGun.Material mat, int v, float[] pal)
        {
            int o = v * HdGun.FloatsPerVertex;
            var p = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
            Vector4 a = Vector4.Zero;
            for (int k = 0; k < 4; k++)
            {
                float w = mat.Vertices[o + 15 + k];
                if (w <= 0) continue;
                int bi = (int)mat.Vertices[o + 11 + k] * 16;
                var M = new Matrix4(pal[bi], pal[bi + 1], pal[bi + 2], pal[bi + 3], pal[bi + 4], pal[bi + 5], pal[bi + 6], pal[bi + 7],
                    pal[bi + 8], pal[bi + 9], pal[bi + 10], pal[bi + 11], pal[bi + 12], pal[bi + 13], pal[bi + 14], pal[bi + 15]);
                a += w * (p * M);
            }
            return a.Xyz;
        }

        public int Vertices
        {
            get
            {
                int n = 0;
                foreach (int[] c in Cut) n += c.Length;
                return n;
            }
        }

        // for tests and the log
        public string Describe()
        {
            var per = new List<string>();
            for (int m = 0; m < Cut.Length; m++) if (Cut[m].Length > 0) per.Add($"m{m} {Cut[m].Length / 3}");
            return string.Create(CultureInfo.InvariantCulture, $"tip {Body.Data.Names[Tip]}, {Vertices / 3} triangles ({string.Join(", ", per)}), "
                + $"pose {(_poseClip?.Name ?? "bind")} t={_spec.T:0.00}, {Length:0.00} units behind the muzzle ({Length * Body.Scale:0.000} at body scale), "
                + $"first-person size {Size:0.00} ({Length * Body.Scale * Size:0.000} long)")
                + (_hand != null ? $", hand {_spec.Hand} plays {_handClip!.Name}" : "")
                + (Blast is Vector3 b ? FormattableString.Invariant($", blast at {b.X:0.000} {b.Y:0.000} {b.Z:0.000}") : "")
                + (Light != 1 ? FormattableString.Invariant($", light x{Light:0.00}") : "");
        }

        // cannon space -> MPH's gun space at size `scale` (world units per model unit; the body's own: Body.Scale): the
        // muzzle on MPH's muzzle point (0, 0, MuzzleOffset), moved as MPH's own gun moves (MphGunMotion), for the
        // player's GunDrawTransform to place. `frame`: a turn/shift about the muzzle (world units) on top
        public Matrix4 GunSpace(float scale, float muzzleOffset, Matrix4 motion, Matrix4 frame) =>
            ToCannon * Matrix4.CreateScale(scale) * frame * Matrix4.CreateTranslation(0, 0, muzzleOffset) * motion;
    }

    // How MPH's own first-person gun moves (its shot recoil, charge-shot kick, the raise after a respawn or unmorph): one
    // node of the hunter's gun model (WeavelGun "topit", the part that kicks back on a shot; SamusGun "gun", the whole gun)
    // in the gun state and frame the player's gun plays now, against its pose at the start of Idle -- the cut cannons ride
    // it rigidly. Its own copy of the model (uncached), so the player's gun model is never touched: drawing only.
    public sealed class MphGunMotion
    {
        readonly Hunter _hunter;
        readonly ModelInstance _inst;
        readonly int _node;
        readonly Matrix4 _restInv;
        int _anim = -1;

        MphGunMotion(Hunter hunter, ModelInstance inst, int node, Matrix4 restInv)
        {
            _hunter = hunter;
            _inst = inst;
            _node = node;
            _restInv = restInv;
        }

        public static MphGunMotion For(Hunter hunter)
        {
            ModelInstance inst = Read.GetModelInstance(Metadata.HunterModels[hunter][3], noCache: true);
            IReadOnlyList<Node> nodes = inst.Model.Nodes;
            int node = Math.Min(1, nodes.Count - 1);
            for (int i = 0; i < nodes.Count; i++) if (nodes[i].Name == "topit") node = i;
            var m = new MphGunMotion(hunter, inst, node, Matrix4.Identity);
            Matrix4 rest = m.Evaluate(GunAnimation.Idle, 0);
            return new MphGunMotion(hunter, inst, node, rest.Inverted());
        }

        public string NodeName => _inst.Model.Nodes[_node].Name;

        // the node's motion from its rest (gun space, row vectors: a point at rest p goes to p * Delta)
        public Matrix4 Delta(PlayerEntity p) => _restInv * Evaluate(p.GunAnimation, p.GunAnimFrame);

        public Matrix4 Delta(GunAnimation state, int frame) => _restInv * Evaluate(state, frame);

        Matrix4 Evaluate(GunAnimation state, int frame)
        {
            int id = Metadata.GunAnimationIds[(int)_hunter, (int)state, 0];
            if (id < 0) id = Metadata.GunAnimationIds[(int)_hunter, (int)GunAnimation.Idle, 0];
            if (id != _anim)
            {
                _inst.SetAnimation(id, slot: 0, SetFlags.Node);
                _anim = id;
            }
            AnimationInfo info = _inst.AnimInfo;
            info.Frame[0] = Math.Clamp(frame, 0, Math.Max(0, info.FrameCount[0] - 1));
            Model model = _inst.Model;
            model.ComputeNodeMatrices(0);
            model.AnimateNodes(0, false, Matrix4.Identity, model.Scale, info);
            return model.Nodes[_node].Animation;
        }
    }
}
