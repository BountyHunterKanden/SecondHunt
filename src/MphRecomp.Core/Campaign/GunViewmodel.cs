using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // A Metroid Prime first-person arm cannon (exported from the owner's own disc by gun_export.py: skeleton, skinned
    // per-material vertices in GxShader's layout, every animation sampled per key -- already in MPH's gun space:
    // X left, Y up, Z forward) driven by MPH's own gun state, to be drawn in place of the game's SamusGun.
    public sealed class HdGun
    {
        public sealed class Material
        {
            public int[] Palette = Array.Empty<int>();   // palette slot -> bone index
            public float[] Vertices = Array.Empty<float>(); // 19 floats per vertex (see GxShader / gun_export.py)
            public int VertexCount;
        }

        public sealed class Anim
        {
            public string Name = "";
            public float Duration, Tick;
            public int Keys;
            public float[] Frames = Array.Empty<float>(); // [key][bone] quat xyzw + translation xyz
        }

        public int BoneCount;
        public int[] Parent = Array.Empty<int>();
        public Vector3[] Bind = Array.Empty<Vector3>();
        // model space -> bone space at bind (row vectors). MPHGUN1: a translation (Retro binds carry no rotation);
        // MPHGUN2 (Beyond): the full inverse bind matrix, rotations and all
        public Matrix4[] InvBind = Array.Empty<Matrix4>();
        // floats per bone per key: 7 = quat + translation (MPHGUN1), 10 = + scale xyz (MPHGUN2)
        public int KeyStride = 7;
        // gun.cfg "root <16 floats>": model space -> the Prime-1-shaped gun space the placement expects (row vectors);
        // "muzzle x y z": where the muzzle sits at rest in that space, for guns with no LBEAM bone
        public Matrix4 Root = Matrix4.Identity;
        public Vector3? Muzzle;
        public string[] Names = Array.Empty<string>();
        public Material[] Materials = Array.Empty<Material>();
        public readonly Dictionary<string, Anim> Anims = new();
        public int[] Order = Array.Empty<int>(); // parents before children
        // gun.cfg next to gun.bin: "lights <material ...>" (the materials that take MPH's per-weapon light colour) and
        // "scale <s>" (the Prime gun's size in MPH's gun space; muzzles are aligned regardless)
        public int[] LightMaterials = Array.Empty<int>();
        public float Scale = 0.6f;
        // "glow <unit>": a gun whose lights share their materials with the plating (Beyond's: an incandescence map added
        // in its own TEV stage) -- only what that texture unit reads takes the colour, in every material that has it
        public int GlowUnit = -1;

        // how a material takes MPH's light colour: 0 not at all, 1 the whole material, 2 only texture unit GlowUnit
        public float TintMode(int material, bool hasGlowLayer)
        {
            if (Array.IndexOf(LightMaterials, material) >= 0) return 1;
            return GlowUnit >= 0 && hasGlowLayer ? 2 : 0;
        }

        public const int FloatsPerVertex = 19;

        public int BoneIndex(string name) => Array.IndexOf(Names, name);

        public static HdGun Load(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            string magic = Encoding.ASCII.GetString(r.ReadBytes(8));
            if (magic != "MPHGUN1\0" && magic != "MPHGUN2\0")
            {
                throw new InvalidDataException($"{path}: not a gun.bin");
            }
            bool v2 = magic == "MPHGUN2\0";
            var g = new HdGun { KeyStride = v2 ? 10 : 7 };
            string Str() => Encoding.Latin1.GetString(r.ReadBytes(r.ReadByte()));
            g.BoneCount = r.ReadInt32();
            g.Parent = new int[g.BoneCount]; g.Bind = new Vector3[g.BoneCount]; g.Names = new string[g.BoneCount];
            g.InvBind = new Matrix4[g.BoneCount];
            for (int i = 0; i < g.BoneCount; i++)
            {
                g.Parent[i] = r.ReadInt32();
                if (v2)
                {
                    var f = new float[16];
                    for (int k = 0; k < 16; k++) f[k] = r.ReadSingle();
                    g.InvBind[i] = new Matrix4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
                    g.Bind[i] = g.InvBind[i].Inverted().ExtractTranslation();
                }
                else
                {
                    g.Bind[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    g.InvBind[i] = Matrix4.CreateTranslation(-g.Bind[i]);
                }
                g.Names[i] = Str();
            }
            g.Materials = new Material[r.ReadInt32()];
            for (int m = 0; m < g.Materials.Length; m++)
            {
                var mat = new Material { Palette = new int[r.ReadInt32()] };
                for (int k = 0; k < mat.Palette.Length; k++)
                {
                    mat.Palette[k] = r.ReadInt32();
                }
                mat.VertexCount = r.ReadInt32();
                mat.Vertices = new float[mat.VertexCount * FloatsPerVertex];
                for (int k = 0; k < mat.Vertices.Length; k++)
                {
                    mat.Vertices[k] = r.ReadSingle();
                }
                g.Materials[m] = mat;
            }
            int anims = r.ReadInt32();
            for (int a = 0; a < anims; a++)
            {
                var an = new Anim { Name = Str(), Duration = r.ReadSingle(), Tick = r.ReadSingle(), Keys = r.ReadInt32() };
                an.Frames = new float[an.Keys * g.BoneCount * g.KeyStride];
                for (int k = 0; k < an.Frames.Length; k++)
                {
                    an.Frames[k] = r.ReadSingle();
                }
                g.Anims[an.Name] = an;
            }
            var order = new List<int>();
            var done = new bool[g.BoneCount];
            void Visit(int b)
            {
                if (done[b]) return;
                if (g.Parent[b] >= 0) Visit(g.Parent[b]);
                done[b] = true; order.Add(b);
            }
            for (int b = 0; b < g.BoneCount; b++) Visit(b);
            g.Order = order.ToArray();
            string cfg = Path.Combine(Path.GetDirectoryName(path) ?? "", "gun.cfg");
            if (File.Exists(cfg))
            {
                foreach (string line in File.ReadAllLines(cfg))
                {
                    string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length >= 2 && t[0] == "lights")
                    {
                        g.LightMaterials = t[1..].Select(x => Int32.Parse(x)).ToArray();
                    }
                    else if (t.Length >= 2 && t[0] == "scale")
                    {
                        g.Scale = Single.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (t.Length >= 2 && t[0] == "glow")
                    {
                        g.GlowUnit = Int32.Parse(t[1]);
                    }
                    else if (t.Length >= 17 && t[0] == "root")
                    {
                        float[] f = t[1..17].Select(x => Single.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        g.Root = new Matrix4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
                    }
                    else if (t.Length >= 4 && t[0] == "muzzle")
                    {
                        float F(string x) => Single.Parse(x, System.Globalization.CultureInfo.InvariantCulture);
                        g.Muzzle = new Vector3(F(t[1]), F(t[2]), F(t[3]));
                    }
                }
            }
            return g;
        }

        // the local (rotation, translation) of a bone at time t, interpolated between keys
        public void Sample(Anim a, float t, int bone, out Quaternion rot, out Vector3 trans) => Sample(a, t, bone, out rot, out trans, out _);

        // ... and its scale (1 for MPHGUN1)
        public void Sample(Anim a, float t, int bone, out Quaternion rot, out Vector3 trans, out Vector3 scale)
        {
            t = Math.Clamp(t, 0, a.Duration);
            float kf = a.Tick > 0 ? t / a.Tick : 0;
            int lo = Math.Min((int)kf, a.Keys - 1), hi = Math.Min(lo + 1, a.Keys - 1);
            float f = Math.Clamp(kf - lo, 0, 1);
            int o0 = (lo * BoneCount + bone) * KeyStride, o1 = (hi * BoneCount + bone) * KeyStride;
            var q0 = new Quaternion(a.Frames[o0], a.Frames[o0 + 1], a.Frames[o0 + 2], a.Frames[o0 + 3]);
            var q1 = new Quaternion(a.Frames[o1], a.Frames[o1 + 1], a.Frames[o1 + 2], a.Frames[o1 + 3]);
            rot = Quaternion.Slerp(q0, q1, f);
            trans = Vector3.Lerp(new Vector3(a.Frames[o0 + 4], a.Frames[o0 + 5], a.Frames[o0 + 6]),
                new Vector3(a.Frames[o1 + 4], a.Frames[o1 + 5], a.Frames[o1 + 6]), f);
            scale = KeyStride < 10 ? Vector3.One : Vector3.Lerp(new Vector3(a.Frames[o0 + 7], a.Frames[o0 + 8], a.Frames[o0 + 9]),
                new Vector3(a.Frames[o1 + 7], a.Frames[o1 + 8], a.Frames[o1 + 9]), f);
        }
    }

    public sealed class GunViewmodel
    {
        public readonly HdGun Gun;
        readonly HdGun.Anim _base, _shoot, _chargeUp, _chargeLoop, _chargeShoot, _missOpen, _missShoot, _missAway, _toHolster, _fromHolster;
        readonly bool[] _hatchBones; // bones the missile hatch moves (from the end of missile-open)
        public readonly Matrix4[] World;
        public readonly int MuzzleBone, ChargeBone;

        HdGun.Anim? _oneShot;
        float _oneShotTime, _loopTime;
        GunAnimation _prevState = GunAnimation.Idle;
        int _prevFrame;
        public string Playing { get; private set; } = "";

        public GunViewmodel(HdGun gun, string beam = "power")
        {
            Gun = gun;
            HdGun.Anim Get(params string[] names)
            {
                foreach (string n in names)
                {
                    if (gun.Anims.TryGetValue(n, out HdGun.Anim? a)) return a;
                }
                throw new KeyNotFoundException($"gun animation {names[0]}");
            }
            _base = Get(beam + "BasePosition", "powerBasePosition");
            _shoot = Get(beam + "Shoot");
            _chargeUp = Get(beam + "ChargeUp");
            _chargeLoop = Get(beam + "ChargeLoop");
            _chargeShoot = Get(beam + "ChargeShoot", "new" + char.ToUpper(beam[0]) + beam[1..] + "ChargeShoot");
            _missOpen = Get(beam + "MissleOpen");
            _missShoot = Get("genericMissleShoot");
            _missAway = Get(beam + "MissleAway");
            _toHolster = Get(beam + "ToHolster");
            _fromHolster = Get(beam + "FromHolster");
            World = new Matrix4[gun.BoneCount];
            // the hatch: bones whose pose at the end of missile-open differs from the resting pose
            _hatchBones = new bool[gun.BoneCount];
            for (int b = 0; b < gun.BoneCount; b++)
            {
                gun.Sample(_base, 0, b, out Quaternion r0, out Vector3 t0);
                gun.Sample(_missOpen, _missOpen.Duration, b, out Quaternion r1, out Vector3 t1);
                float dot = r0.X * r1.X + r0.Y * r1.Y + r0.Z * r1.Z + r0.W * r1.W;
                float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(dot), 0, 1));
                _hatchBones[b] = angle > 0.05f || (t1 - t0).Length > 0.005f;
            }
            MuzzleBone = gun.BoneIndex("LBEAM");
            ChargeBone = gun.BoneIndex("LCHRG");
        }

        public int HatchBoneCount { get { int n = 0; foreach (bool h in _hatchBones) if (h) n++; return n; } }

        // advance with MPH's gun state (read after the simulation step)
        public void Update(PlayerEntity player, float dt)
        {
            GunAnimation state = player.GunAnimation;
            int frame = player.GunAnimFrame, count = player.GunAnimFrameCount;
            float ratio = count > 1 ? Math.Clamp(frame / (float)(count - 1), 0, 1) : 0;
            bool restarted = state != _prevState || frame < _prevFrame;
            _prevState = state; _prevFrame = frame;
            // shots play out on Prime's own clock (MPH's shot state is shorter than Prime's shot animation)
            if (restarted)
            {
                HdGun.Anim? start = state switch
                {
                    GunAnimation.Shot => _shoot,
                    GunAnimation.ChargeShot => _chargeShoot,
                    GunAnimation.MissileShot or GunAnimation.Unknown9 => _missShoot,
                    _ => null
                };
                if (start != null)
                {
                    _oneShot = start; _oneShotTime = 0;
                }
                else if (state != GunAnimation.Idle)
                {
                    _oneShot = null;
                }
            }
            _loopTime += dt;
            if (_oneShot != null)
            {
                _oneShotTime += dt;
                bool holdEnd = _oneShot == _missShoot;
                if (_oneShotTime < _oneShot.Duration || holdEnd && state is GunAnimation.MissileShot or GunAnimation.Unknown9)
                {
                    Pose(_oneShot, MathF.Min(_oneShotTime, _oneShot.Duration));
                    return;
                }
                _oneShot = null;
            }
            switch (state)
            {
            case GunAnimation.Charging:
                Pose(_chargeUp, ratio * _chargeUp.Duration); break;
            case GunAnimation.FullCharge:
                Pose(_chargeLoop, _loopTime % _chargeLoop.Duration); break;
            case GunAnimation.MissileOpen:
                Pose(_missOpen, ratio * _missOpen.Duration); break;
            case GunAnimation.MissileClose:
                Pose(_missAway, ratio * _missAway.Duration); break;
            case GunAnimation.ChargingMissile:
                Pose(_chargeUp, ratio * _chargeUp.Duration, hatchOpen: true); break;
            case GunAnimation.FullChargeMissile:
                Pose(_chargeLoop, _loopTime % _chargeLoop.Duration, hatchOpen: true); break;
            case GunAnimation.Switch:
                if (ratio < 0.5f) Pose(_toHolster, ratio * 2 * _toHolster.Duration);
                else Pose(_fromHolster, (ratio - 0.5f) * 2 * _fromHolster.Duration);
                break;
            case GunAnimation.UpDown:
                Pose(_fromHolster, ratio * _fromHolster.Duration); break;
            case GunAnimation.MissileShot or GunAnimation.Unknown9:
                Pose(_missShoot, _missShoot.Duration); break;
            default:
                Pose(_base, 0); break;
            }
        }

        // world pose; hatchOpen: the missile hatch bones held at the end of missile-open (MPH's charging-missile states,
        // which Prime has no animation for) while the rest follows the given animation
        void Pose(HdGun.Anim a, float t, bool hatchOpen = false)
        {
            Playing = hatchOpen ? a.Name + " + hatch open" : a.Name;
            HdGun g = Gun;
            foreach (int b in g.Order)
            {
                Quaternion r; Vector3 p, s;
                if (hatchOpen && _hatchBones[b]) g.Sample(_missOpen, _missOpen.Duration, b, out r, out p, out s);
                else g.Sample(a, t, b, out r, out p, out s);
                Matrix4 local = Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(p);
                if (s != Vector3.One)
                {
                    local = Matrix4.CreateScale(s) * local;
                }
                World[b] = g.Parent[b] >= 0 ? local * World[g.Parent[b]] : local;
            }
        }

        // bone position in gun space
        public Vector3 BonePosition(int bone) => bone >= 0 ? World[bone].ExtractTranslation() : Vector3.Zero;

        // skin matrices for one material's palette, row-vector (OpenTK) layout, 16 floats each -- uploaded untransposed
        public void FillPalette(int material, float[] dst)
        {
            int[] pal = Gun.Materials[material].Palette;
            for (int k = 0; k < pal.Length; k++)
            {
                int b = pal[k];
                Matrix4 m = Gun.InvBind[b] * World[b];
                int o = k * 16;
                dst[o] = m.M11; dst[o + 1] = m.M12; dst[o + 2] = m.M13; dst[o + 3] = m.M14;
                dst[o + 4] = m.M21; dst[o + 5] = m.M22; dst[o + 6] = m.M23; dst[o + 7] = m.M24;
                dst[o + 8] = m.M31; dst[o + 9] = m.M32; dst[o + 10] = m.M33; dst[o + 11] = m.M34;
                dst[o + 12] = m.M41; dst[o + 13] = m.M42; dst[o + 14] = m.M43; dst[o + 15] = m.M44;
            }
        }

        // model -> world: uniform scale, then shift so the Prime muzzle bone lands on MPH's muzzle, then MPH's gun matrix
        // (the muzzle flash, charge glow and beam spawn all come from MPH's muzzle point)
        public Matrix4 ModelMatrix(PlayerEntity player, float scale, Vector3 restMuzzle)
        {
            const float mphMuzzleForward = 1548 / 4096f; // Samus's MuzzleOffset (Metadata Player.cs)
            Vector3 shift = new Vector3(0, 0, mphMuzzleForward) - restMuzzle * scale;
            return Gun.Root * Matrix4.CreateScale(scale) * Matrix4.CreateTranslation(shift) * player.GunDrawTransform;
        }

        // MPH's own first-person gun among the scene's draw items (the host draws the Prime gun in its place)
        public static bool IsMphGunItem(Scene scene, RenderItem item)
        {
            return item.Type == RenderItemType.Mesh && item.ListId >= 1 && item.ListId <= scene.HostMeshes.Count
                && scene.HostMeshes[item.ListId - 1].Model.Name == "SamusGun";
        }

        // the colour MPH gives the gun's lights for the current weapon: its per-weapon material animation (slot 1)
        // drives material "lambert10" (Power/Volt/Missile amber, Battlehammer green, Imperialist red, Judicator violet,
        // Magmaul orange, Shock Coil blue). False when MPH drew no first-person gun this frame.
        public static bool TryMphLightColour(Scene scene, out Vector3 colour)
        {
            colour = Vector3.One;
            bool drawn = false;
            foreach (IReadOnlyList<RenderItem> list in new[] { scene.OpaqueItems, scene.DecalItems, scene.TranslucentItems })
            {
                for (int i = 0; i < list.Count; i++)
                {
                    RenderItem item = list[i];
                    if (!IsMphGunItem(scene, item))
                    {
                        continue;
                    }
                    drawn = true;
                    (Model model, Mesh mesh, bool _) = scene.HostMeshes[item.ListId - 1];
                    if (model.Materials[mesh.MaterialId].Name == "lambert10")
                    {
                        colour = item.Diffuse;
                        return true;
                    }
                }
            }
            return drawn;
        }

        // where the muzzle bone sits at rest (the base pose)
        public Vector3 RestMuzzle()
        {
            Pose(_base, 0);
            return Gun.Muzzle ?? Vector3.TransformPosition(BonePosition(MuzzleBone), Gun.Root);
        }
    }
}
