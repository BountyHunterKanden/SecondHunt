using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;

namespace MphRecomp.Import.Guns
{
    // What one first-person gun folder is made from, whichever Retro game it came from (GunImporter: Prime 1 / Echoes;
    // Gun3Importer: Corruption), and the writer both share (the common half of gun_export.py and gun_export3.py):
    //   gun.bin      "MPHGUN1": skeleton + per-material skinned vertices (GxShader layout) + every animation sampled per key
    //   gun.gx.json  each material's GX program (GxJson.Read)
    //   gun.cfg      "lights <materials>" + "scale <s>"
    //   <tex>.png    the textures, stored upside down like the trophies' (UVs stay Retro's)
    // Coordinates are converted from Retro's (X right, Y forward, Z up) to MPH's gun space (X left, Y up, Z forward):
    // (x, y, z) -> (-x, z, y), a proper rotation, so rotations convert as q -> (C * axis, w).

    // a GX program as gun.gx.json and the texture export read it: Retro's stored one (Prime 1 / Echoes) or one converted
    // from Corruption's texture passes (Material3Gx)
    public sealed class GunMaterial
    {
        public uint Flags;
        public ulong[] TexIds = Array.Empty<ulong>();
        public int[][] Konst = Array.Empty<int[]>();   // rgba 0..255
        public int BlendSrc, BlendDst;
        public uint[] Channels = Array.Empty<uint>();
        public Cmdl.TevStage[] Stages = Array.Empty<Cmdl.TevStage>();
        public Cmdl.TexGen[] TexGens = Array.Empty<Cmdl.TexGen>();
        public Cmdl.UvAnim[] UvAnims = Array.Empty<Cmdl.UvAnim>();

        public static GunMaterial From(Cmdl.Material m) => new()
        {
            Flags = m.Flags, TexIds = m.TexIds.Select(t => (ulong)t).ToArray(), Konst = m.Konst.Select(k => k.Select(x => (int)x).ToArray()).ToArray(),
            BlendSrc = m.BlendSrc, BlendDst = m.BlendDst, Channels = m.Channels, Stages = m.Stages, TexGens = m.TexGens, UvAnims = m.UvAnims,
        };

        public static GunMaterial From(GxMaterial3 m) => new()
        {
            Flags = m.Flags, TexIds = m.TexIds, Konst = m.Konst, BlendSrc = m.BlendSrc, BlendDst = m.BlendDst, Channels = m.Channels,
            Stages = m.Stages, TexGens = m.TexGens, UvAnims = m.UvAnims,
        };
    }

    // one model drawn with the gun: its arrays, its triangles' corners, its materials, and each corner's skin as
    // (CINF bone id, weight > 0) pairs
    public sealed class GunMesh
    {
        public float[] Positions = Array.Empty<float>(), Normals = Array.Empty<float>(), Uv0 = Array.Empty<float>();
        public Cmdl.Corner[] Triangles = Array.Empty<Cmdl.Corner>();
        public int[] TriangleMaterial = Array.Empty<int>();
        public List<GunMaterial> Materials = new();
        public Func<Cmdl.Corner, IEnumerable<(uint Bone, float Weight)>> SkinOf = _ => Array.Empty<(uint, float)>();

        public static GunMesh From(Cmdl m) => new()
        {
            Positions = m.Positions, Normals = m.Normals, Uv0 = m.Uv0, Triangles = m.Triangles, TriangleMaterial = m.TriangleMaterial,
            Materials = m.Materials.Select(GunMaterial.From).ToList(),
        };
    }

    // the skeleton in gun.bin's order (bone ids ascending) and MPH's gun space
    public sealed class GunSkeleton
    {
        public Cinf Cinf = null!;
        public List<uint> BoneIds = new();
        public Dictionary<uint, int> Index = new();
        public double[][] Bind = Array.Empty<double[]>();
        public int[] Parents = Array.Empty<int>();
        public string[] Names = Array.Empty<string>();

        public static double[] Conv(double x, double y, double z) => new[] { -x, z, y };

        public static GunSkeleton From(Cinf skel)
        {
            var g = new GunSkeleton { Cinf = skel, BoneIds = skel.SortedIds() };
            int n = g.BoneIds.Count;
            for (int i = 0; i < n; i++) g.Index[g.BoneIds[i]] = i;
            g.Bind = new double[n][]; g.Parents = new int[n]; g.Names = new string[n];
            for (int i = 0; i < n; i++)
            {
                Cinf.Bone b = skel.Bones[g.BoneIds[i]];
                g.Bind[i] = Conv(b.X, b.Y, b.Z);
                g.Parents[i] = g.Index.TryGetValue(b.Parent, out int p) ? p : -1;
                g.Names[i] = String.IsNullOrEmpty(b.Name) ? $"bone{g.BoneIds[i]}" : b.Name;
            }
            return g;
        }
    }

    public sealed class GunSource
    {
        public GunSkeleton Skeleton = null!;
        public readonly List<GunMesh> Meshes = new();
        // in gun.bin's order; each loaded when it is written (bone ids = the CINF's)
        public readonly List<(string Name, Func<Retro.Anim> Load)> Animations = new();
        public Func<ulong, byte[]> Texture = _ => throw new KeyNotFoundException();
        public string Source = "";     // gun.gx.json's "source"
        public int[] Lights = Array.Empty<int>();
    }

    public static class GunWriter
    {
        // MPH's own gun, back of the forearm to the muzzle, in its gun space (-guntest measured 0.335 from z 0.031 to the
        // muzzle at 0.378): the Prime gun is scaled so its elbow-to-muzzle length matches
        public const double MphGunLength = 0.3483;

        public static void Write(GunSource src, string outDir, GunImporter.Result result, Action<float, string>? progress = null,
            CancellationToken ct = default)
        {
            void Say(float f, string msg)
            {
                result.Log.Add(msg);
                progress?.Invoke(f, msg);
            }
            Directory.CreateDirectory(outDir);
            GunSkeleton sk = src.Skeleton;
            List<uint> boneIds = sk.BoneIds;
            Dictionary<uint, int> bix = sk.Index;
            double[][] bind = sk.Bind;
            string[] names = sk.Names;
            Cinf skel = sk.Cinf;

            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("MPHGUN1\0"));
            void Name(string s)
            {
                byte[] b = Encoding.Latin1.GetBytes(s);
                int n = Math.Min(b.Length, 255);
                w.Write((byte)n);
                w.Write(b, 0, n);
            }
            void F3(double[] v)
            {
                w.Write((float)v[0]); w.Write((float)v[1]); w.Write((float)v[2]);
            }

            w.Write((uint)boneIds.Count);
            for (int i = 0; i < boneIds.Count; i++)
            {
                w.Write(sk.Parents[i]);
                F3(bind[i]);
                Name(names[i]);
            }

            // materials: per-material vertex arrays, 19 floats per vertex (RenderActivity's GpuSkinFloats layout for
            // GxShader): pos3 normal3 uv2 colour3 (r, g, packed b+a) bones4 (palette-local) weights4
            const float whiteBa = (float)(255 + 0.999);
            var allMats = new List<GunMaterial>();
            w.Write((uint)src.Meshes.Sum(p => p.Materials.Count));
            foreach (GunMesh model in src.Meshes)
            {
                float[] P = model.Positions, N = model.Normals, UV = model.Uv0;
                int nuv = UV.Length / 2;
                for (int mi = 0; mi < model.Materials.Count; mi++)
                {
                    allMats.Add(model.Materials[mi]);
                    var corners = new List<Cmdl.Corner>();
                    for (int t = 0; t < model.TriangleMaterial.Length; t++)
                    {
                        if (model.TriangleMaterial[t] != mi) continue;
                        corners.Add(model.Triangles[3 * t]); corners.Add(model.Triangles[3 * t + 1]); corners.Add(model.Triangles[3 * t + 2]);
                    }
                    var skins = corners.Select(c => model.SkinOf(c).Select(bw => (Bone: bix[bw.Bone], bw.Weight)).ToList()).ToList();
                    var palette = new SortedSet<int>(skins.SelectMany(s => s.Select(bw => bw.Bone))).ToList();
                    if (palette.Count > 32) throw new InvalidDataException($"material {mi}: {palette.Count} bones");
                    var local = new Dictionary<int, int>();
                    for (int k = 0; k < palette.Count; k++) local[palette[k]] = k;
                    w.Write((uint)palette.Count);
                    foreach (int g in palette) w.Write(g);
                    w.Write((uint)corners.Count);
                    for (int ci = 0; ci < corners.Count; ci++)
                    {
                        Cmdl.Corner c = corners[ci];
                        w.Write(-P[3 * c.P]); w.Write(P[3 * c.P + 2]); w.Write(P[3 * c.P + 1]);
                        w.Write(-N[3 * c.N]); w.Write(N[3 * c.N + 2]); w.Write(N[3 * c.N + 1]);
                        if (c.T0 >= 0)
                        {
                            if (c.T0 >= nuv) throw new InvalidDataException($"uv index {c.T0} of {nuv}");
                            w.Write(UV[2 * c.T0]); w.Write(UV[2 * c.T0 + 1]);
                        }
                        else
                        {
                            w.Write(0f); w.Write(0f);
                        }
                        w.Write(1f); w.Write(1f); w.Write(whiteBa);
                        List<(int Bone, float Weight)> ws = skins[ci];
                        for (int k = 0; k < 4; k++) w.Write(k < ws.Count ? (float)local[ws[k].Bone] : 0f);
                        for (int k = 0; k < 4; k++) w.Write(k < ws.Count ? ws[k].Weight : 0f);
                    }
                }
            }
            Say(0.3f, $"materials: {allMats.Count}");
            ct.ThrowIfCancellationRequested();

            // animations: every key, every bone -> local rotation quat (x y z w) + local translation, converted
            w.Write((uint)src.Animations.Count);
            int done = 0;
            foreach ((string nm, Func<Retro.Anim> load) in src.Animations)
            {
                ct.ThrowIfCancellationRequested();
                Retro.Anim A = load();
                Name(nm);
                w.Write((float)A.Duration); w.Write((float)A.Tick);
                w.Write((uint)A.Keys);
                for (int k = 0; k < A.Keys; k++)
                {
                    A.Sample(k * A.Tick, out Dictionary<uint, double[]> rot, out Dictionary<uint, double[]> trans);
                    foreach (uint b in boneIds)
                    {
                        if (rot.TryGetValue(b, out double[]? q))
                        {
                            w.Write((float)-q[0]); w.Write((float)q[2]); w.Write((float)q[1]); w.Write((float)q[3]);
                        }
                        else
                        {
                            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);
                        }
                        if (trans.TryGetValue(b, out double[]? t))
                        {
                            F3(GunSkeleton.Conv(t[0], t[1], t[2]));
                        }
                        else
                        {
                            Cinf.Bone bone = skel.Bones[b];
                            double px = 0, py = 0, pz = 0;
                            if (skel.Bones.TryGetValue(bone.Parent, out Cinf.Bone? par))
                            {
                                px = par.X; py = par.Y; pz = par.Z;
                            }
                            F3(GunSkeleton.Conv(bone.X - px, bone.Y - py, bone.Z - pz));
                        }
                    }
                }
                done++;
                progress?.Invoke(0.3f + 0.5f * done / src.Animations.Count, $"animation {nm}");
            }
            w.Flush();
            File.WriteAllBytes(Path.Combine(outDir, "gun.bin"), ms.ToArray());
            result.Bones = boneIds.Count; result.Materials = allMats.Count; result.Animations = src.Animations.Count; result.Bytes = (int)ms.Length;
            Say(0.8f, $"gun.bin: {boneIds.Count} bones, {allMats.Count} materials, {src.Animations.Count} animations, {ms.Length / 1024.0:F0} KB");

            // gun.cfg: the lights, and the scale that makes the elbow-to-muzzle length MPH's
            int lb = Array.IndexOf(names, "LBEAM");
            if (lb < 0) throw new InvalidDataException("the gun skeleton has no 'LBEAM'");
            double[] muzzle = bind[lb];
            double scale = MphGunLength / Math.Sqrt(muzzle[0] * muzzle[0] + muzzle[1] * muzzle[1] + muzzle[2] * muzzle[2]);
            var cfg = new StringBuilder();
            string lights = String.Join(" ", src.Lights);
            if (lights.Length > 0) cfg.Append("lights ").Append(lights).Append('\n');
            cfg.Append("scale ").Append(scale.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "gun.cfg"), cfg.ToString());
            result.Scale = scale;

            // textures (upside down, like the trophies)
            var texIds = new SortedSet<ulong>(allMats.SelectMany(m => m.TexIds));
            foreach (ulong tid in texIds)
            {
                ct.ThrowIfCancellationRequested();
                byte[] T = src.Texture(tid);
                byte[]? rgba = Txtr.Decode(T, out uint fmt, out int tw, out int th);
                if (rgba != null)
                {
                    PngOut.Write(Path.Combine(outDir, $"{tid:x8}.png"), Txtr.FlipRows(rgba, tw, th), tw, th);
                    result.Textures++;
                }
                else
                {
                    Say(0.9f, $"  texture {tid:x8}: format {fmt} not decoded");
                }
            }

            // materials as GX programs -- same conversion as prime_to_trophy.py (see GxJson.cs)
            var gx = new List<object?>();
            for (int mi = 0; mi < allMats.Count; mi++)
            {
                GunMaterial mm = allMats[mi];
                var konst = new List<object?>();
                foreach (int[] c in mm.Konst) konst.Add(c.Select(x => (object?)(x / 255.0)).ToList());
                for (int k = 0; k < 4; k++) konst.Add(new[] { 0, 0, 0, 0 });
                var stages = new List<object?>();
                foreach (Cmdl.TevStage st in mm.Stages)
                {
                    stages.Add(new PyJson.Obj
                    {
                        { "c", st.ColorIn }, { "cop", st.ColorOp.Op }, { "cbias", st.ColorOp.Bias }, { "cscale", st.ColorOp.Scale }, { "cclamp", st.ColorOp.Clamp }, { "cdest", st.ColorOp.Out },
                        { "a", st.AlphaIn }, { "aop", st.AlphaOp.Op }, { "abias", st.AlphaOp.Bias }, { "ascale", st.AlphaOp.Scale }, { "aclamp", st.AlphaOp.Clamp }, { "adest", st.AlphaOp.Out },
                        { "texmap", st.Tex == 255 ? -1 : st.Tex }, { "texcoord", st.TexCoord }, { "ras", st.Ras == 4 ? 0 : st.Ras == 5 ? 1 : 7 },
                        { "kc", st.KColor }, { "ka", st.KAlpha },
                    });
                }
                var layers = new List<object?>();
                for (int ti = 0; ti < mm.TexIds.Length; ti++)
                {
                    List<int> tcs = mm.Stages.Where(s => s.Tex == ti).Select(s => s.TexCoord).ToList();
                    int srcSel = 4, mtx = 30;
                    if (tcs.Count > 0 && tcs[0] < mm.TexGens.Length)
                    {
                        srcSel = mm.TexGens[tcs[0]].Src; mtx = mm.TexGens[tcs[0]].Mtx;
                    }
                    bool env = srcSel is 0 or 1;
                    object?[] scroll = { 0.0, 0.0 };
                    Cmdl.UvAnim? anim = null;
                    if (mtx != 30 && mtx / 3 < mm.UvAnims.Length) anim = mm.UvAnims[mtx / 3];
                    if (anim != null && anim.Mode == 2) scroll = new object?[] { anim.Params[2], -anim.Params[3] };
                    if (anim != null && anim.Mode is 0 or 1) env = true;
                    layers.Add(new PyJson.Obj
                    {
                        { "name", $"{mm.TexIds[ti]:x8}" }, { "texmap", ti }, { "texcoord", tcs.Count > 0 ? tcs[0] : ti }, { "env", env }, { "scroll", scroll },
                        { "mtx", new[] { 1, 0, 0, 0, 1, 0 } },
                    });
                }
                int bs = mm.BlendSrc, bd = mm.BlendDst;
                bool blend = !(bs == 1 && bd == 0);
                gx.Add(new PyJson.Obj
                {
                    { "name", $"gun{mi}" }, { "stages", stages }, { "konst", konst.Take(4).ToList() },
                    { "lit", mm.Channels.Length > 0 && (mm.Channels[0] & 1) != 0 }, { "layers", layers },
                    { "blend", blend }, { "blendSrc", bs }, { "blendDst", bd }, { "depthWrite", !blend },
                    { "alphaTest", (mm.Flags & 0x20) != 0 }, { "cull", 2 },
                });
            }
            var root = new PyJson.Obj
            {
                { "source", src.Source },
                { "materials", gx },
            };
            File.WriteAllText(Path.Combine(outDir, "gun.gx.json"), PyJson.Dump(root));
            Say(1f, $"gun.gx.json: {gx.Count} materials ({allMats.Count(m => !(m.BlendSrc == 1 && m.BlendDst == 0))} blended); {texIds.Count} textures");
        }
    }
}
