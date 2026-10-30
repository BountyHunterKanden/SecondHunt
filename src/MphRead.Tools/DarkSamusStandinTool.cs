using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // Limoki's Dark Samus stand-in (a Blender edit of MPH's own Samus lod0) turned into a build-from-ROM recipe, so the
    // model ships without any ROM geometry or pixels:
    //   -dsstandin dump <glb>                 ROM Samus_lod0 layout + the glb's primitives
    //   -dsstandin match <glb>                how her corners line up with the ROM corners (bind space, per bone)
    //   -dsstandin emit <glb> out=<dir>       writes recipe.json (ROM corner refs + deltas, her new parts) and her PNGs
    //   -dsstandin check <dir>                builds the model from the ROM + <dir> through Core and prints its layout
//   -dsstandin float                      Dark Samus's float pose (PlayerHostFloat.cs) on Samus_lod0: joints + toe heights
    //   -dsstandin corr <bodyDir>             our Corruption Dark Samus (imported body) in the same measures, for matching
    // The recipe keeps for each of her triangle corners either a ROM corner (mesh, decoded corner index) plus the small
    // change she made to it (bone-local position, uv, colour), or, for her new parts, her own vertex. Triangles she
    // removed are simply never referenced.
    internal static class DarkSamusStandinTool
    {
        const float PosEps = 0.002f;

        public static void Run(string[] args)
        {
            string mode = args.Length > 1 ? args[1] : "match";
            if (mode == "corr")
            {
                Corr(Path.GetFullPath(args[2]));
                return;
            }
            if (mode == "float")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                Float();
                return;
            }
            string? path = args.Length > 2 ? args[2] : null;
            string? outDir = null, glbPath = null, skinPath = null, source = null;
            foreach (string arg in args.Skip(3))
            {
                if (arg.StartsWith("out=")) outDir = arg[4..];
                else if (arg.StartsWith("glb=")) glbPath = Path.GetFullPath(arg[4..]);
                else if (arg.StartsWith("skin=")) skinPath = Path.GetFullPath(arg[5..]);
                else if (arg.StartsWith("src=")) source = arg[4..];
                else throw new ArgumentException($"unknown option {arg}");
            }
            if (path == null) throw new ArgumentException("missing <glb> / <dir>");
            path = Path.GetFullPath(path);
            if (outDir != null) outDir = Path.GetFullPath(outDir);
            if (!ToolPaths.UseDesktopExtraction()) return;
            Model rom = Read.GetModelInstance(source ?? DarkSamusStandin.SourceModel).Model;
            rom.ComputeNodeMatrices(0);
            if (mode == "rom")
            {
                Dump(rom, null); // the source model alone (src=SamusAlt_lod0: the ball's host)
                return;
            }
            if (mode == "check")
            {
                Check(path, glbPath != null ? GlbMesh.Read(glbPath, skinPath) : null);
                return;
            }
            GlbMesh glb = GlbMesh.Read(path, skinPath);
            if (mode == "dump") Dump(rom, glb);
            else if (mode == "match") Match(rom, glb, null);
            else if (mode == "emit")
            {
                if (outDir == null) throw new ArgumentException("emit needs out=<dir>");
                Match(rom, glb, outDir);
            }
            else throw new ArgumentException($"unknown mode {mode}");
        }

        // the float pose run on Samus's idle frame 0: rotation sense, segment directions (degrees from straight down,
        // forward + / out +), the toe tips' heights over a bob cycle, and the glide leans
        static void Float()
        {
            Model m = Read.GetModelInstance(DarkSamusStandin.SourceModel).Model;
            var q = Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(-90));
            Console.WriteLine($"sense: +X turned -90 about Y: Vector3.Transform {Vector3.Transform(Vector3.UnitX, q)}, row-vector matrix "
                + $"{Vector3.TransformPosition(Vector3.UnitX, Matrix4.CreateFromQuaternion(q))} (want both (0,0,-1)... same sign = consistent)");
            var info = new AnimationInfo();
            NodeAnimationGroup idle = m.AnimationGroups.Node[(int)Entities.PlayerAnimation.Idle];
            info.Node.Group = idle;
            info.Index[0] = (int)Entities.PlayerAnimation.Idle;
            info.FrameCount[0] = idle.FrameCount;
            int N(string name) => m.Nodes.ToList().FindIndex(n => n.Name == name);
            Vector3 At(string name) => m.Nodes[N(name)].Animation.ExtractTranslation();
            Vector3 toeL = new Vector3(0.298f, 0.0596f, 0.0066f), toeR = new Vector3(0.298f, 0.0596f, -0.0066f);
            string HeadMeasure()
            {
                // MPH Head_1: forward = local Z, up = local Y (as the Corruption rig's)
                Matrix4 r = m.Nodes[N("Head_1")].Animation.ClearTranslation();
                Vector3 f = Vector3.TransformVector(Vector3.UnitZ, r).Normalized(), u = Vector3.TransformVector(Vector3.UnitY, r).Normalized();
                return $"head: down {MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(-f.Y, -1, 1))),5:F1} "
                    + $"left {MathHelper.RadiansToDegrees(MathF.Atan2(-f.X, -f.Z)),5:F1} roll {MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(u.X, -1, 1))),5:F1}";
            }
            Vector3 Tip() => Vector3.TransformPosition(new Vector3(0.3f, 0, 0), m.Nodes[N("R_elbow")].Animation);
            void Pose(float w, float t, Vector2 dir, float aim, bool full)
            {
                info.Frame[0] = 0;
                m.AnimateNodes(0, false, Matrix4.Identity, Vector3.One, info);
                Vector4 lean = Entities.PlayerEntity.FloatLeanWeights(dir);
                Entities.PlayerEntity.ApplyHostFloatPose(m, w, t, lean, aim);
                Vector3 tl = Vector3.TransformPosition(toeL, m.Nodes[N("L_ankle")].Animation);
                Vector3 tr = Vector3.TransformPosition(toeR, m.Nodes[N("R_ankle")].Animation);
                Console.WriteLine($"w {w:F2} t {t:F2} dir {dir} lean {lean} aim {aim}: pelvis {At("Pelvis")} head {At("Head_1")} toeL {tl} toeR {tr} cannon {Seg(At("R_elbow"), Tip())}");
                if (!full) return;
                Vector3 M(string n) => n == "toeL" ? Vector3.TransformPosition(toeL, m.Nodes[N("L_ankle")].Animation)
                    : n == "toeR" ? Vector3.TransformPosition(toeR, m.Nodes[N("R_ankle")].Animation)
                    : n == "tip" ? Tip() : At(n);
                Measure("  same measures", M, n => N(n) >= 0, "Spine_2", "Spine_2", "Head_1", "toeL", "toeR", "tip");
                Console.WriteLine("  " + HeadMeasure());
            }
            info.Frame[0] = 0;
            m.AnimateNodes(0, false, Matrix4.Identity, Vector3.One, info);
            for (int i = 0; i < m.Nodes.Count; i++)
            {
                Node n = m.Nodes[i];
                Console.WriteLine($"node {i} {n.Name} parent {n.ParentIndex} at {n.Animation.ExtractTranslation()}");
            }
            Pose(0, 0, Vector2.Zero, 0, true);
            // the aim: vanilla's spine tilt (drawBiped) against ours, the cannon's direction
            foreach (float aim in new[] { 30f, -30f })
            {
                Node spine = m.Nodes[N("Spine_1")];
                spine.AfterTransform = DetMatrix.CreateRotationZ(MathHelper.DegreesToRadians(aim));
                info.Frame[0] = 0;
                m.AnimateNodes(0, false, Matrix4.Identity, Vector3.One, info);
                spine.AfterTransform = null;
                Console.WriteLine($"vanilla aim {aim}: cannon {Seg(At("R_elbow"), Tip())} head {At("Head_1")}");
                Pose(1, 0, Vector2.Zero, aim, false);
            }
            Pose(1, 0, Vector2.Zero, 0, true);
            float meanPelvis = 0;
            for (int i = 0; i < 8; i++)
            {
                Pose(1, 1f * i / 8, Vector2.Zero, 0, false);
                meanPelvis += At("Pelvis").Y / 8;
            }
            Console.WriteLine($"standing mean pelvis y {meanPelvis:F3}");
            Pose(1, 0, new Vector2(0, 1), 0, true);
            Pose(1, 0, new Vector2(0, -1), 0, true);
            Pose(1, 0, new Vector2(-1, 0), 0, true);
            Pose(1, 0, new Vector2(1, 0), 0, true);
            Pose(1, 0, new Vector2(0.7f, 0.7f), 0, false);
            Pose(1, 0, new Vector2(0, 0.5f), 0, false);
            Pose(0.5f, 0, Vector2.Zero, 0, false);
        }

        // the shared measures (positions in MPH model axes: +X her right, +Y up, -Z forward), degrees: segments as forward /
        // out from straight down; the hips' and shoulders' line as yaw (+ = facing turned to her left) and drop (+ = her
        // right side lower); the head's own axes
        static string Seg(Vector3 a, Vector3 b)
        {
            Vector3 d = (b - a).Normalized();
            float fwd = MathHelper.RadiansToDegrees(MathF.Atan2(-d.Z, -d.Y));
            float side = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(d.X, -1, 1)));
            return $"{fwd,6:F1}/{side,6:F1}";
        }

        static string Line(Vector3 left, Vector3 right)
        {
            Vector3 v = (right - left).Normalized();
            return $"yaw {MathHelper.RadiansToDegrees(MathF.Atan2(-v.Z, v.X)),5:F1} drop {MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(-v.Y, -1, 1))),5:F1}";
        }

        static void Measure(string label, Func<string, Vector3> at, Func<string, bool> has, string top, string neck, string head, string toeL, string toeR, string tip)
        {
            Console.WriteLine($"{label}: pelvis {at("Pelvis")} hips {Line(at("L_hip"), at("R_hip"))} shoulders {Line(at("L_shoulder"), at("R_shoulder"))}");
            Console.WriteLine($"  pelvis up {Seg((at("L_hip") + at("R_hip")) / 2, at("Spine_1"))}");
            Console.WriteLine($"  spine {Seg(at("Pelvis"), at(top))} upper spine {Seg(at("Spine_1"), at(top))} neck {Seg(at(neck), at(head))}");
            foreach (string s in new[] { "L", "R" })
            {
                Console.WriteLine($"  {s} leg: thigh {Seg(at(s + "_hip"), at(s + "_knee"))} shin {Seg(at(s + "_knee"), at(s + "_ankle"))} foot {Seg(at(s + "_ankle"), at(s == "L" ? toeL : toeR))}"
                    + $"  toe y {at(s == "L" ? toeL : toeR).Y,6:F3}");
            }
            Console.WriteLine($"  L arm {Seg(at("L_shoulder"), at("L_elbow"))} forearm {Seg(at("L_elbow"), at("L_wrist"))}"
                + $"  R arm {Seg(at("R_shoulder"), at("R_elbow"))} cannon {Seg(at("R_elbow"), at(tip))}");
        }

        static void Corr(string dir)
        {
            NativeBody b = NativeBody.Load(dir);
            BodyClips map = BodyClips.For(b) ?? throw new InvalidDataException("no clip map");
            HdGun g = b.Data;
            string Has(params string[] names) => names.FirstOrDefault(n => b.Bone(n) >= 0) ?? throw new InvalidDataException($"no bone {names[0]}");
            for (int i = 0; i < g.BoneCount; i++) Console.Write($"{g.Names[i]}({g.Parent[i]}) ");
            Console.WriteLine();
            // Corruption axes (+X her left, +Z forward) to MPH's; scaled to MPH's size by the height (MPH Samus ~1.7 tall)
            float scale = NativeBody.PlayerHeight / b.Height;
            Vector3 At(string n) { Vector3 p = b.World[b.Bone(n == "Pelvis" ? "Skeleton_Root" : n)].ExtractTranslation(); return new Vector3(-p.X, p.Y - b.Feet, -p.Z) * scale; }
            string top = Has("Spine_2", "Spine_3", "Neck_1", "Neck"), neck = Has("Neck_1", "Neck", "Spine_3", "Spine_2"), head = Has("Head_1", "Head");
            string toeL = Has("L_ball", "L_toe", "L_toe_1", "L_foot"), toeR = Has("R_ball", "R_toe", "R_toe_1", "R_foot");
            string tip = DarkSamusBody.TipOf(b);
            Console.WriteLine($"scale {scale:F3} top {top} neck {neck} head {head} toes {toeL} {toeR} tip {tip} idle {map.Idle}");
            void Head(string label)
            {
                // the head's own axes (Corruption binds have no rotation): its forward's pitch (+ = down) and yaw (+ = to her
                // left), its up's roll (+ = top toward her right)
                Matrix4 r = b.World[b.Bone(head)].ClearTranslation();
                Vector3 f = Vector3.TransformVector(Vector3.UnitZ, r), u = Vector3.TransformVector(Vector3.UnitY, r);
                Console.WriteLine($"  head{label}: down {MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(-f.Y, -1, 1))),5:F1} "
                    + $"left {MathHelper.RadiansToDegrees(MathF.Atan2(f.X, f.Z)),5:F1} roll {MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(-u.X, -1, 1))),5:F1}");
            }
            void One(HdGun.Anim a, float t, string label)
            {
                b.Pose(a, t);
                map.AfterPose?.Invoke(b, null);
                Measure($"{label} t {t:F2}", At, n => b.Bone(n) >= 0, top, neck, head, toeL, toeR, tip);
                Head("");
            }
            HdGun.Anim idle = b.Clip(map.Idle)!;
            Console.WriteLine($"idle {idle.Name} {idle.Duration:F2} s");
            for (int i = 0; i < 8; i++) One(idle, idle.Duration * i / 8, "idle");
            if (map.Lean is (string f0, string b0, string l0, string r0))
            {
                foreach (string c in new[] { f0, b0, l0, r0 })
                {
                    HdGun.Anim a = b.Clip(c)!;
                    Console.WriteLine($"{c} {a.Duration:F2} s");
                    for (int i = 0; i < 4; i++) One(a, a.Duration * i / 4, c);
                }
            }
        }

        // one decoded ROM corner, in the ROM's bone-local space and in bind space
        sealed class RomCorner
        {
            public int Mesh, Index, Node;
            public Vector3 Local, World, Normal;
            public Vector4 Color;
            public Vector2 Uv;
        }

        static List<RomCorner> RomCorners(Model rom)
        {
            var list = new List<RomCorner>();
            for (int m = 0; m < rom.Meshes.Count; m++)
            {
                float[] v = MphRecomp.Render.DsDisplayList.Decode(rom, rom.Meshes[m], isRoom: false);
                for (int i = 0; i < v.Length / 13; i++)
                {
                    int o = i * 13;
                    int node = rom.NodeMatrixIds[(int)v[o + 12]];
                    var local = new Vector3(v[o], v[o + 1], v[o + 2]);
                    list.Add(new RomCorner
                    {
                        Mesh = m, Index = i, Node = node, Local = local,
                        World = Vector3.TransformPosition(local, rom.Nodes[node].Transform) * rom.Scale.X,
                        Normal = new Vector3(v[o + 3], v[o + 4], v[o + 5]),
                        Color = new Vector4(v[o + 6], v[o + 7], v[o + 8], v[o + 9]),
                        Uv = new Vector2(v[o + 10], v[o + 11]),
                    });
                }
            }
            return list;
        }

        static void Dump(Model rom, GlbMesh? glb)
        {
            Console.WriteLine($"== ROM {rom.Name}: scale {rom.Scale} nodes {rom.Nodes.Count} meshes {rom.Meshes.Count} materials {rom.Materials.Count} matrix ids {rom.NodeMatrixIds.Count}");
            for (int i = 0; i < rom.Nodes.Count; i++)
            {
                Node n = rom.Nodes[i];
                Console.WriteLine($"  node {i} '{n.Name}' parent {n.ParentIndex} meshes {n.MeshCount}@{n.MeshId} bind {n.Transform.Row3.Xyz}");
            }
            // the feet: the toe tip (most forward low corner) on each ankle, in bind and ankle-local space (the stand-in float's
            // toe point, PlayerHostFloat.cs)
            foreach (RomCorner c in RomCorners(rom).Where(c => rom.Nodes[c.Node].Name.EndsWith("_ankle")).GroupBy(c => c.Node)
                .Select(g => g.OrderBy(c => c.World.Z + c.World.Y * 0.5f).First()))
            {
                Console.WriteLine($"  toe on '{rom.Nodes[c.Node].Name}': bind {c.World} local {c.Local}");
            }
            foreach (IGrouping<int, RomCorner> g in RomCorners(rom).Where(c => rom.Nodes[c.Node].Name.EndsWith("_ankle")).GroupBy(c => c.Node))
            {
                Console.WriteLine($"  foot '{rom.Nodes[g.Key].Name}': y {g.Min(c => c.World.Y)}..{g.Max(c => c.World.Y)} z {g.Min(c => c.World.Z)}..{g.Max(c => c.World.Z)}");
            }
            for (int i = 0; i < rom.Meshes.Count; i++)
            {
                Mesh m = rom.Meshes[i];
                float[] v = MphRecomp.Render.DsDisplayList.Decode(rom, m, isRoom: false);
                Console.WriteLine($"  mesh {i}: material {m.MaterialId} '{rom.Materials[m.MaterialId].Name}' tris {v.Length / 13 / 3}");
            }
            foreach (IGrouping<int, RomCorner> g in RomCorners(rom).GroupBy(c => c.Mesh))
            {
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                foreach (RomCorner c in g) { min = Vector3.ComponentMin(min, c.World); max = Vector3.ComponentMax(max, c.World); }
                Console.WriteLine($"  mesh {g.Key} bind bb {min}..{max} nodes {String.Join(" ", g.Select(c => c.Node).Distinct().Order())}");
            }
            for (int i = 0; i < rom.Materials.Count; i++)
            {
                Material m = rom.Materials[i];
                string tex = m.TextureId >= 0 ? $"{rom.Recolors[0].Textures[m.TextureId].Format} {rom.Recolors[0].Textures[m.TextureId].Width}x{rom.Recolors[0].Textures[m.TextureId].Height}" : "none";
                Console.WriteLine($"  mat {i} '{m.Name}': tex {m.TextureId} ({tex}) pal {m.PaletteId} light {m.Lighting} alpha {m.Alpha} cull {m.Culling} poly {m.PolygonMode} render {m.RenderMode} texgen {m.TexgenMode} mtx {m.MatrixId} scale {m.ScaleS},{m.ScaleT} tr {m.TranslateS},{m.TranslateT} rot {m.RotateZ} diff {m.Diffuse.Red},{m.Diffuse.Green},{m.Diffuse.Blue} amb {m.Ambient.Red},{m.Ambient.Green},{m.Ambient.Blue}");
            }
            Console.WriteLine($"  recolors {rom.Recolors.Count}: {String.Join(", ", rom.Recolors.Select(r => r.Name))}");
            if (glb == null)
            {
                Console.WriteLine($"  matrix ids {String.Join(" ", rom.NodeMatrixIds)}; anim groups node {rom.AnimationGroups.Node.Count} material {rom.AnimationGroups.Material.Count} texcoord {rom.AnimationGroups.Texcoord.Count} texture {rom.AnimationGroups.Texture.Count}");
                return;
            }
            for (int i = 0; i < glb.NodeNames.Length; i++)
            {
                int rn = rom.GetNodeIndexByName(glb.NodeNames[i]);
                Matrix4 gw = glb.NodeWorld[i];
                string rs = rn >= 0 ? $"{rom.Nodes[rn].Transform.Row3.Xyz} scale {rom.Nodes[rn].Scale} rows {rom.Nodes[rn].Transform.Row0.Xyz} {rom.Nodes[rn].Transform.Row1.Xyz}" : "-";
                Console.WriteLine($"  glb node {i} '{glb.NodeNames[i]}' world {gw.Row3.Xyz} rows {gw.Row0.Xyz} {gw.Row1.Xyz} | rom {rs}");
            }
            List<RomCorner> rc = RomCorners(rom);
            foreach (GlbMesh.Prim p in glb.Prims.Take(1))
            {
                for (int k = 0; k < 6; k++)
                {
                    int ix = p.Indices[k];
                    int rn = rom.GetNodeIndexByName(glb.NodeNames[p.Joint[ix]]);
                    RomCorner? c = rc.Where(c => c.Node == rn).MinBy(c => (c.Local - p.Skin[ix]).Length);
                    Console.WriteLine($"    '{glb.NodeNames[p.Joint[ix]]}' skin {p.Skin[ix]} uv {p.Uv[ix]} | nearest rom local {c?.Local} uv {c?.Uv}");
                }
                foreach (RomCorner c in rc.Where(c => c.Mesh == 0).Take(6)) Console.WriteLine($"    rom node {c.Node} local {c.Local} uv {c.Uv}");
            }
            Console.WriteLine($"== glb: {glb.Prims.Count} primitives, {glb.Images.Count} images, {glb.NodeNames.Length} nodes");
            foreach (GlbMesh.Prim p in glb.Prims)
            {
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                foreach (int ix in p.Indices) { min = Vector3.ComponentMin(min, p.World[ix]); max = Vector3.ComponentMax(max, p.World[ix]); }
                Console.WriteLine($"  prim '{p.Material}' image {p.Image} tris {p.Indices.Length / 3} verts {p.World.Length} colour sets {p.Colors.Count} bb {min}..{max}");
            }
        }

        // her corner: bind-space position, joint (MPH node index), uv (MPH convention), colour
        static void Match(Model rom, GlbMesh glb, string? outDir)
        {
            List<RomCorner> corners = RomCorners(rom);
            // compared bone-local: her glb sits rotated 180 degrees about Y from MphRead's bind and its joints are orthonormal
            // (the ROM's elbow rest matrices are sheared), but each vertex through its own joint's inverse bind lands in the
            // ROM node's space: unedited corners match the ROM's stored vertex exactly
            int romNodeOf(int glbNode) => rom.GetNodeIndexByName(glb.NodeNames[glbNode]);
            var byNode = corners.GroupBy(c => c.Node).ToDictionary(g => g.Key, g => g.ToList());
            var recipeMeshes = new List<RecipeMesh>();
            int totalRef = 0, totalNew = 0;
            foreach (GlbMesh.Prim p in glb.Prims)
            {
                int exact = 0, moved = 0, none = 0, uvOff = 0, colOff = 0;
                float maxMove = 0;
                var meshCount = new Dictionary<int, int>();
                var refs = new List<int[]>();
                var deltas = new List<float[]>();
                var colourSet = ColourSetFor(p, corners, byNode, romNodeOf);
                bool newPart = p.Material is "Visor" or "Shoulders";
                foreach (int ix in p.Indices)
                {
                    int node = romNodeOf(p.Joint[ix]);
                    Vector3 local = p.Skin[ix];
                    Vector2 uv = p.Uv[ix];
                    Vector4 col = colourSet >= 0 ? p.Colors[colourSet][ix] : Vector4.One;
                    RomCorner? best = null;
                    float bestScore = float.MaxValue;
                    if (!newPart && byNode.TryGetValue(node, out List<RomCorner>? cand))
                    {
                        foreach (RomCorner c in cand)
                        {
                            float d = (c.Local - local).Length;
                            float score = d + (c.Uv - uv).Length * 0.01f + (c.Color.Xyz - col.Xyz).Length * 0.001f;
                            if (score < bestScore) { bestScore = score; best = c; }
                        }
                    }
                    if (best != null && (best.Local - local).Length < 0.25f)
                    {
                        float d = (best.Local - local).Length;
                        maxMove = MathF.Max(maxMove, d);
                        if (d < PosEps) exact++; else moved++;
                        if ((best.Uv - uv).Length > 0.002f) uvOff++;
                        if (colourSet >= 0 && (best.Color.Xyz - col.Xyz).Length > 0.02f) colOff++;
                        meshCount[best.Mesh] = meshCount.GetValueOrDefault(best.Mesh) + 1;
                        Vector3 dp = d < PosEps ? Vector3.Zero : local - best.Local;
                        Vector2 duv = (best.Uv - uv).Length > 0.002f ? uv - best.Uv : Vector2.Zero;
                        Vector3 dc = colourSet >= 0 && (best.Color.Xyz - col.Xyz).Length > 0.02f ? col.Xyz - best.Color.Xyz : Vector3.Zero;
                        refs.Add(new[] { best.Mesh, best.Index });
                        deltas.Add(new[] { dp.X, dp.Y, dp.Z, duv.X, duv.Y, dc.X, dc.Y, dc.Z });
                        totalRef++;
                    }
                    else
                    {
                        none++;
                        totalNew++;
                        Vector3 nrm = p.SkinNormal[ix];
                        refs.Add(new[] { -1, node });
                        deltas.Add(new[] { local.X, local.Y, local.Z, uv.X, uv.Y, col.X, col.Y, col.Z, nrm.X, nrm.Y, nrm.Z });
                    }
                }
                Console.WriteLine($"  prim '{p.Material}': {p.Indices.Length / 3} tris, colour set {colourSet}; corners exact {exact} moved {moved} new {none}; uv changed {uvOff} colour changed {colOff}; max move {maxMove:0.000}; from ROM meshes {String.Join(" ", meshCount.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"))}");
                recipeMeshes.Add(new RecipeMesh(p.Material, p.Image, BaseMaterial(rom, meshCount), refs, deltas, p.GlowImage, p.GlowStrength));
            }
            // which ROM triangles she dropped (info only: the recipe lists what she kept)
            var used = new HashSet<(int, int)>();
            Console.WriteLine($"  corners: {totalRef} from ROM, {totalNew} her own");
            if (outDir == null) return;
            Directory.CreateDirectory(outDir);
            string[] imageFiles = new string[glb.Images.Count];
            for (int i = 0; i < glb.Images.Count; i++)
            {
                string file = Sanitize(glb.Images[i].Name) + ".png";
                File.WriteAllBytes(Path.Combine(outDir, file), glb.Images[i].Png);
                imageFiles[i] = file;
            }
            WriteRecipe(Path.Combine(outDir, "recipe.json"), rom, glb, recipeMeshes, imageFiles);
        }

        sealed record RecipeMesh(string Material, int Image, string Base, List<int[]> Refs, List<float[]> Deltas, int Glow, float GlowStrength);

        // the ROM material her mesh copies its settings from: the one most of its corners came from (new parts: the body's)
        static string BaseMaterial(Model rom, Dictionary<int, int> meshCount)
        {
            int mesh = meshCount.Count > 0 ? meshCount.MaxBy(k => k.Value).Key
                : Enumerable.Range(0, rom.Meshes.Count).MaxBy(m => rom.RenderInstructionLists[rom.Meshes[m].DlistId].Count);
            return rom.Materials[rom.Meshes[mesh].MaterialId].Name;
        }

        static string Sanitize(string name) => new(name.Select(c => Char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

        // the colour set whose values best match the ROM vertex colours on corners that line up (-1: none)
        static int ColourSetFor(GlbMesh.Prim p, List<RomCorner> corners, Dictionary<int, List<RomCorner>> byNode, Func<int, int> romNodeOf)
        {
            int best = -1;
            float bestErr = float.MaxValue;
            for (int s = 0; s < p.Colors.Count; s++)
            {
                float err = 0;
                int n = 0;
                foreach (int ix in p.Indices.Take(300))
                {
                    if (!byNode.TryGetValue(romNodeOf(p.Joint[ix]), out List<RomCorner>? cand)) continue;
                    RomCorner? c = cand.Where(c => (c.Local - p.Skin[ix]).Length < PosEps).MinBy(c => (c.Uv - p.Uv[ix]).Length);
                    if (c == null) continue;
                    err += (c.Color.Xyz - p.Colors[s][ix].Xyz).Length;
                    n++;
                }
                if (n > 0 && err / n < bestErr) { bestErr = err / n; best = s; }
            }
            return best;
        }

        static void WriteRecipe(string file, Model rom, GlbMesh glb, List<RecipeMesh> meshes, string[] images)
        {
            string R(float f) => MathF.Abs(f) < 1e-6f ? "0" : f.ToString("0.#####", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"comment\": \"Dark Samus stand-in by Limoki, built at load from the user's own {DarkSamusStandin.SourceModel}. refs: [rom mesh, rom decoded corner] + deltas [dx,dy,dz (bone-local), du,dv, dr,dg,db]; or [-1, node] + her own vertex [x,y,z (bone-local), u,v, r,g,b, nx,ny,nz].\",\n");
            sb.Append($"  \"source\": \"{DarkSamusStandin.SourceModel}\",\n");
            sb.Append($"  \"sourceHash\": \"{DarkSamusStandin.SourceHash(rom)}\",\n");
            sb.Append("  \"meshes\": [\n");
            for (int m = 0; m < meshes.Count; m++)
            {
                RecipeMesh mesh = meshes[m];
                List<int[]> refs = mesh.Refs;
                List<float[]> deltas = mesh.Deltas;
                string glow = mesh.Glow >= 0 ? $", \"glow\": \"{images[mesh.Glow]}\", \"glowStrength\": {R(mesh.GlowStrength)}" : "";
                sb.Append($"    {{ \"material\": \"{mesh.Material}\", \"base\": \"{mesh.Base}\", \"image\": \"{images[mesh.Image]}\"{glow}, \"corners\": [\n");
                for (int i = 0; i < refs.Count; i++)
                {
                    float[] d = deltas[i];
                    int n = d.Length;
                    if (refs[i][0] >= 0) { while (n > 0 && MathF.Abs(d[n - 1]) < 1e-6f) n--; }
                    string tail = n > 0 ? ", " + String.Join(", ", d.Take(n).Select(R)) : "";
                    sb.Append($"      [{refs[i][0]}, {refs[i][1]}{tail}]{(i + 1 < refs.Count ? "," : "")}\n");
                }
                sb.Append($"    ] }}{(m + 1 < meshes.Count ? "," : "")}\n");
            }
            sb.Append("  ]\n}\n");
            File.WriteAllText(file, sb.ToString());
            Console.WriteLine($"  wrote {file} ({new FileInfo(file).Length} bytes) + {images.Length} PNGs");
        }

        // builds the model from the ROM + recipe; with her glb, compares every built corner back to her vertex
        static void Check(string dir, GlbMesh? glb)
        {
            Model? model = DarkSamusStandin.Build(dir, DarkSamusStandin.ModelNames[0], log: Console.WriteLine);
            if (model == null)
            {
                Console.WriteLine("  [FAIL] build returned null");
                return;
            }
            Console.WriteLine($"  built '{model.Name}': nodes {model.Nodes.Count} meshes {model.Meshes.Count} materials {model.Materials.Count} recolors {model.Recolors.Count} textures {model.Recolors[0].Textures.Count}");
            for (int i = 0; i < model.Meshes.Count; i++)
            {
                Mesh m = model.Meshes[i];
                float[] v = MphRecomp.Render.DsDisplayList.Decode(model, m, isRoom: false);
                Material mat = model.Materials[m.MaterialId];
                Console.WriteLine($"  mesh {i}: material {m.MaterialId} '{mat.Name}' tex {mat.TextureId} light {mat.Lighting} texgen {mat.TexgenMode} mtx {mat.MatrixId} scale {mat.ScaleS},{mat.ScaleT} poly {mat.PolygonMode} render {mat.RenderMode} tris {v.Length / 13 / 3}");
                // a glow overlay (X_glow) repeats prim X's corners
                string primName = mat.Name.EndsWith("_glow", StringComparison.Ordinal) ? mat.Name[..^5] : mat.Name;
                GlbMesh.Prim? p = glb?.Prims.FirstOrDefault(q => q.Material == primName);
                if (glb == null) continue;
                if (p == null || p.Indices.Length * 13 != v.Length)
                {
                    Console.WriteLine($"    [FAIL] no glb primitive with {v.Length / 13} corners");
                    continue;
                }
                float maxPos = 0, maxUv = 0;
                int wrongBone = 0;
                for (int c = 0; c < p.Indices.Length; c++)
                {
                    int ix = p.Indices[c], o = c * 13;
                    maxPos = MathF.Max(maxPos, (new Vector3(v[o], v[o + 1], v[o + 2]) - p.Skin[ix]).Length);
                    maxUv = MathF.Max(maxUv, (new Vector2(v[o + 10], v[o + 11]) - p.Uv[ix]).Length);
                    if (model.NodeMatrixIds[(int)v[o + 12]] != model.GetNodeIndexByName(glb.NodeNames[p.Joint[ix]])) wrongBone++;
                }
                bool ok = maxPos < 0.001f && maxUv < 0.01f && wrongBone == 0;
                Console.WriteLine($"    {(ok ? "[OK]" : "[FAIL]")} vs glb: max position error {maxPos:0.00000}, max uv error {maxUv:0.0000}, wrong bone {wrongBone}");
            }
        }

        // the skinned mesh of a glTF binary in bind pose: positions through each vertex's joint, uv, colours, images
        sealed class GlbMesh
        {
            public sealed class Prim
            {
                public string Material = "";
                public int Image = -1;
                public int[] Indices = Array.Empty<int>();
                public Vector3[] World = Array.Empty<Vector3>(), Normal = Array.Empty<Vector3>();
                public Vector3[] Skin = Array.Empty<Vector3>(), SkinNormal = Array.Empty<Vector3>(); // in the joint's own space (inverse bind only)
                public Vector2[] Uv = Array.Empty<Vector2>();
                public int[] Joint = Array.Empty<int>(); // glb node index of the vertex's (single) joint
                public readonly List<Vector4[]> Colors = new();
                public Vector3[] Raw = Array.Empty<Vector3>(); // the glb's own mesh-space position (the skin graft's key)
                public Matrix4[] VertIbm = Array.Empty<Matrix4>(), VertJoint = Array.Empty<Matrix4>(); // the joint's inverse bind, joint world * inverse bind
                public int GlowImage = -1; // emissive texture (her glow map), -1 none
                public float GlowStrength = 1; // emissiveFactor (grey) * KHR_materials_emissive_strength
            }

            public sealed class Image
            {
                public string Name = "";
                public byte[] Png = Array.Empty<byte>();
            }

            public readonly List<Prim> Prims = new();
            public readonly List<Image> Images = new();
            public string[] NodeNames = Array.Empty<string>();
            public Matrix4[] NodeWorld = Array.Empty<Matrix4>();

            // skinPath: a glb of the same mesh with an armature, for one exported without its skin (her v2): each vertex takes
            // the joint of the donor's vertex at the same mesh-space position
            public static GlbMesh Read(string path, string? skinPath = null)
            {
                byte[] b = File.ReadAllBytes(path);
                int jsonLen = BitConverter.ToInt32(b, 12);
                using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(b, 20, jsonLen));
                int binAt = 20 + jsonLen + 8;
                JsonElement root = doc.RootElement;
                JsonElement nodes = root.GetProperty("nodes"), acc = root.GetProperty("accessors"), views = root.GetProperty("bufferViews");
                int n = nodes.GetArrayLength();
                var g = new GlbMesh { NodeNames = new string[n] };
                var parent = Enumerable.Repeat(-1, n).ToArray();
                var local = new Matrix4[n];
                for (int i = 0; i < n; i++)
                {
                    JsonElement e = nodes[i];
                    g.NodeNames[i] = e.TryGetProperty("name", out JsonElement nm) ? nm.GetString() ?? "" : "";
                    Vector3 t = e.TryGetProperty("translation", out JsonElement te) ? new Vector3(F(te, 0), F(te, 1), F(te, 2)) : Vector3.Zero;
                    Vector3 s = e.TryGetProperty("scale", out JsonElement se) ? new Vector3(F(se, 0), F(se, 1), F(se, 2)) : Vector3.One;
                    Quaternion r = e.TryGetProperty("rotation", out JsonElement re) ? new Quaternion(F(re, 0), F(re, 1), F(re, 2), F(re, 3)) : Quaternion.Identity;
                    local[i] = Matrix4.CreateScale(s) * Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(t);
                    if (e.TryGetProperty("children", out JsonElement ch)) foreach (JsonElement c in ch.EnumerateArray()) parent[c.GetInt32()] = i;
                }
                var world = new Matrix4[n];
                Matrix4 World(int i) => parent[i] < 0 ? local[i] : local[i] * World(parent[i]);
                for (int i = 0; i < n; i++) world[i] = World(i);
                g.NodeWorld = world;

                float[] Acc(int a, out int comps)
                {
                    JsonElement x = acc[a];
                    JsonElement v = views[x.GetProperty("bufferView").GetInt32()];
                    int count = x.GetProperty("count").GetInt32();
                    comps = x.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new InvalidDataException("accessor type") };
                    int ct = x.GetProperty("componentType").GetInt32();
                    bool norm = x.TryGetProperty("normalized", out JsonElement ne) && ne.GetBoolean();
                    int size = ct switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
                    int stride = v.TryGetProperty("byteStride", out JsonElement bs) ? bs.GetInt32() : size * comps;
                    int off = binAt + (v.TryGetProperty("byteOffset", out JsonElement vo) ? vo.GetInt32() : 0)
                        + (x.TryGetProperty("byteOffset", out JsonElement xo) ? xo.GetInt32() : 0);
                    var o = new float[count * comps];
                    for (int i = 0; i < count; i++)
                    {
                        for (int k = 0; k < comps; k++)
                        {
                            int p = off + i * stride + k * size;
                            o[i * comps + k] = ct switch
                            {
                                5120 => norm ? Math.Max((sbyte)b[p] / 127f, -1) : (sbyte)b[p],
                                5121 => norm ? b[p] / 255f : b[p],
                                5122 => norm ? Math.Max(BitConverter.ToInt16(b, p) / 32767f, -1) : BitConverter.ToInt16(b, p),
                                5123 => norm ? BitConverter.ToUInt16(b, p) / 65535f : BitConverter.ToUInt16(b, p),
                                5125 => BitConverter.ToUInt32(b, p),
                                5126 => BitConverter.ToSingle(b, p),
                                _ => throw new InvalidDataException("component type"),
                            };
                        }
                    }
                    return o;
                }

                // skin: joint world * inverse bind, so the positions land in bind space whatever the mesh node holds
                int[] joints = Array.Empty<int>();
                Matrix4[] jointMat = Array.Empty<Matrix4>(), ibm = Array.Empty<Matrix4>();
                List<(Vector3 Raw, int Joint, Matrix4 Ibm, Matrix4 JointMat)>? graft = null;
                float graftWorst = 0;
                if (root.TryGetProperty("skins", out JsonElement skins))
                {
                    JsonElement skin = skins[0];
                    joints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
                    float[] ibmRaw = Acc(skin.GetProperty("inverseBindMatrices").GetInt32(), out _);
                    jointMat = new Matrix4[joints.Length];
                    ibm = new Matrix4[joints.Length];
                    for (int j = 0; j < joints.Length; j++)
                    {
                        var m = new Matrix4();
                        for (int k = 0; k < 16; k++) m[k / 4, k % 4] = ibmRaw[j * 16 + k]; // column-major = row-vector rows
                        ibm[j] = m;
                        jointMat[j] = m * world[joints[j]];
                    }
                }
                else
                {
                    if (skinPath == null) throw new InvalidDataException($"{Path.GetFileName(path)} has no armature: pass skin=<glb with one>");
                    GlbMesh donor = Read(skinPath);
                    graft = new();
                    foreach (Prim dp in donor.Prims)
                    {
                        for (int i = 0; i < dp.Raw.Length; i++)
                        {
                            int had = graft.FindIndex(d => (d.Raw - dp.Raw[i]).Length < GraftTolerance);
                            if (had >= 0 && graft[had].Joint != dp.Joint[i]) throw new InvalidDataException($"skin donor: two joints at {dp.Raw[i]}");
                            if (had < 0) graft.Add((dp.Raw[i], dp.Joint[i], dp.VertIbm[i], dp.VertJoint[i]));
                        }
                    }
                    g.NodeNames = donor.NodeNames;
                    g.NodeWorld = donor.NodeWorld;
                    Console.WriteLine($"  skin grafted from {Path.GetFileName(skinPath)}: {graft.Count} positions");
                }

                // material -> image
                var matImage = new List<int>();
                var matGlow = new List<(int Image, float Strength)>();
                var matName = new List<string>();
                JsonElement textures = root.GetProperty("textures");
                foreach (JsonElement mat in root.GetProperty("materials").EnumerateArray())
                {
                    matName.Add(mat.GetProperty("name").GetString() ?? "");
                    int img = -1;
                    if (mat.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr) && pbr.TryGetProperty("baseColorTexture", out JsonElement bct))
                    {
                        img = textures[bct.GetProperty("index").GetInt32()].GetProperty("source").GetInt32();
                    }
                    matImage.Add(img);
                    int glow = -1;
                    float strength = 1;
                    if (mat.TryGetProperty("emissiveTexture", out JsonElement et))
                    {
                        glow = textures[et.GetProperty("index").GetInt32()].GetProperty("source").GetInt32();
                        if (mat.TryGetProperty("emissiveFactor", out JsonElement ef)) strength = (F(ef, 0) + F(ef, 1) + F(ef, 2)) / 3;
                        if (mat.TryGetProperty("extensions", out JsonElement ex) && ex.TryGetProperty("KHR_materials_emissive_strength", out JsonElement es))
                        {
                            strength *= es.GetProperty("emissiveStrength").GetSingle();
                        }
                    }
                    matGlow.Add((glow, strength));
                }
                foreach (JsonElement im in root.GetProperty("images").EnumerateArray())
                {
                    JsonElement v = views[im.GetProperty("bufferView").GetInt32()];
                    int off = binAt + (v.TryGetProperty("byteOffset", out JsonElement vo) ? vo.GetInt32() : 0);
                    g.Images.Add(new Image { Name = im.GetProperty("name").GetString() ?? "", Png = b[off..(off + v.GetProperty("byteLength").GetInt32())] });
                }

                foreach (JsonElement mesh in root.GetProperty("meshes").EnumerateArray())
                {
                    foreach (JsonElement pe in mesh.GetProperty("primitives").EnumerateArray())
                    {
                        JsonElement at = pe.GetProperty("attributes");
                        float[] pos = Acc(at.GetProperty("POSITION").GetInt32(), out _);
                        float[] nrm = Acc(at.GetProperty("NORMAL").GetInt32(), out _);
                        float[] uv = Acc(at.GetProperty("TEXCOORD_0").GetInt32(), out _);
                        float[] jn = graft == null ? Acc(at.GetProperty("JOINTS_0").GetInt32(), out _) : Array.Empty<float>();
                        float[] wt = graft == null ? Acc(at.GetProperty("WEIGHTS_0").GetInt32(), out _) : Array.Empty<float>();
                        int vc = pos.Length / 3;
                        var p = new Prim
                        {
                            Indices = Acc(pe.GetProperty("indices").GetInt32(), out _).Select(f => (int)f).ToArray(),
                            World = new Vector3[vc], Normal = new Vector3[vc], Uv = new Vector2[vc], Joint = new int[vc],
                            Skin = new Vector3[vc], SkinNormal = new Vector3[vc],
                            Raw = new Vector3[vc], VertIbm = new Matrix4[vc], VertJoint = new Matrix4[vc],
                        };
                        int mi = pe.GetProperty("material").GetInt32();
                        p.Material = matName[mi];
                        p.Image = matImage[mi];
                        (p.GlowImage, p.GlowStrength) = matGlow[mi];
                        for (int i = 0; i < vc; i++)
                        {
                            var raw = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
                            var rawN = new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
                            p.Raw[i] = raw;
                            if (graft != null)
                            {
                                // nearest donor vertex: re-exports carry float noise (~1e-5), so no exact match
                                int best = -1;
                                float bestDist = float.MaxValue;
                                for (int k = 0; k < graft.Count; k++)
                                {
                                    float dist = (graft[k].Raw - raw).Length;
                                    if (dist < bestDist) (best, bestDist) = (k, dist);
                                }
                                if (bestDist > GraftTolerance) throw new InvalidDataException($"skin donor has no vertex at {raw} ('{p.Material}', nearest {bestDist})");
                                graftWorst = MathF.Max(graftWorst, bestDist);
                                (_, p.Joint[i], p.VertIbm[i], p.VertJoint[i]) = graft[best];
                            }
                            else
                            {
                                int top = 0;
                                for (int k = 1; k < 4; k++) if (wt[i * 4 + k] > wt[i * 4 + top]) top = k;
                                int j = (int)jn[i * 4 + top];
                                p.Joint[i] = joints[j];
                                p.VertIbm[i] = ibm[j];
                                p.VertJoint[i] = jointMat[j];
                            }
                            p.World[i] = Vector3.TransformPosition(raw, p.VertJoint[i]);
                            p.Normal[i] = Vector3.TransformNormal(rawN, p.VertJoint[i]).Normalized();
                            p.Uv[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
                            p.Skin[i] = Vector3.TransformPosition(raw, p.VertIbm[i]);
                            p.SkinNormal[i] = Vector3.TransformNormal(rawN, p.VertIbm[i]).Normalized();
                        }
                        for (int s = 0; at.TryGetProperty($"COLOR_{s}", out JsonElement ce); s++)
                        {
                            float[] c = Acc(ce.GetInt32(), out int cc);
                            var cols = new Vector4[vc];
                            for (int i = 0; i < vc; i++) cols[i] = new Vector4(c[i * cc], c[i * cc + 1], c[i * cc + 2], cc == 4 ? c[i * cc + 3] : 1);
                            p.Colors.Add(cols);
                        }
                        g.Prims.Add(p);
                    }
                }
                if (graft != null) Console.WriteLine($"  graft: worst position gap {graftWorst:0.######}");
                return g;
            }

            static float F(JsonElement a, int i) => a[i].GetSingle();

            // skin graft: a vertex takes the donor joint within this distance (her rig's closest distinct vertices are far wider)
            const float GraftTolerance = 1e-3f;
        }
    }
}
