using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Assets;
using MphRecomp.Render;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // A RIGGED trophy playing the game's animations, textured through the same GX program the Odin uses -- to watch a rig
    // move on the PC the way the owner sees it on the device (seams, bent armour, feet in the floor), frame by frame.
    //   MphRead.Tools.dll -hdanim <Trophy> [--rig default|<variant key>] [--clip Idle] [--frames 0,4,8|all] [--yaw 0,90]
    //                     [--size 512] [--out dir]
    // Writes <out>/<Trophy>_<rig>_<clip>_<frame>_y<yaw>.png. Orthographic, in the game model's space: the same framing for
    // every rig of one hunter (fixed from its standing Idle), a green line where the floor is. yaw = the camera turned about
    // the vertical (0 = the model's front).
    internal static class HdAnim
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            var ci = CultureInfo.InvariantCulture;
            string trophy = args[1];
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string outDir = Arg("--out", Directory.GetCurrentDirectory()); Directory.CreateDirectory(outDir);
            int size = int.Parse(Arg("--size", "512"));
            string key = Arg("--rig", "default");
            // --clip Bind: the rig's bind pose -- the statue as sculpted (or as straightened), not a game clip
            bool bindOnly = Arg("--clip", "Idle").Equals("Bind", StringComparison.OrdinalIgnoreCase);
            int clip = bindOnly ? (int)PlayerAnimation.Idle : (int)Enum.Parse<PlayerAnimation>(Arg("--clip", "Idle"), ignoreCase: true);
            var yaws = Arg("--yaw", "0").Split(',').Select(s => float.Parse(s, ci)).ToArray();
            float amb = float.Parse(Arg("--amb", "0.45"), ci);

            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string dir = Path.Combine(repo, "brawl_extract", "Converted", trophy);
            string dae = Directory.GetFiles(dir, "*.dae").First();
            if (!TrophyRigs.TryHunterFor(trophy, out Hunter h)) { Console.WriteLine($"{trophy}: unknown trophy"); return; }
            TrophyRig? rig = key == "default"
                ? TrophyRigIO.Load(Path.ChangeExtension(dae, ".mphrig"), TrophyRigIO.CacheKey(dae))
                : key == "before" ? TrophyRigIO.Load(TrophyRigIO.BeforePathFor(dae), 0, anyKey: true)   // the rig from before the latest fix
                : key == "posed" ? TrophyRigIO.LoadPosedRig(dae, new DsSkeleton(Read.GetModelInstance(Metadata.HunterModels[h][0]).Model).Names)   // Pose Studio
                : TrophyRigIO.Variants.FirstOrDefault(v => v.Key == key) is { } v ? TrophyRigIO.Load(TrophyRigIO.VariantPathFor(dae, v), TrophyRigIO.VariantKey(dae, v)) : null;
            if (rig == null) { Console.WriteLine($"{trophy}: no current '{key}' rig cache"); return; }
            Model dsModel = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
            var sk = new DsSkeleton(dsModel);
            if (!args.Contains("--nosettle")) TrophyFeet.Settle(rig, dsModel, sk, Console.WriteLine);
            var frames = Arg("--frames", "0") == "all" ? Enumerable.Range(0, dsModel.AnimationGroups.Node[clip].FrameCount).ToList()
                : Arg("--frames", "0").Split(',').Select(int.Parse).ToList();

            string? brres = Directory.GetFiles(dir, "*.brres").FirstOrDefault();
            var model = DaeModel.Load(dae);
            var mats = new Dictionary<string, GxMaterial>();
            foreach (var m in brres != null ? Mdl0Gx.Read(brres) : GxJson.Read(GxJson.PathFor(dae))) mats.TryAdd(m.Name, m);
            var formats = brres != null ? Mdl0Gx.TextureFormats(brres) : new Dictionary<string, int>();

            // the rig's bind (rig space) and each corner's rig vertex; bind normals turned into rig space
            var bind = rig.BindPos ?? rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
            var parts = model.Meshes.Select(p => (p, g: mats.TryGetValue(p.Material, out var gm) ? gm : null)).ToList();
            var cornerV = parts.Select(t => t.p.Positions.Select(q => rig.IndexOf(new System.Numerics.Vector3(q.X, q.Y, q.Z))).ToArray()).ToList();
            var cornerN = parts.Select((t, pi) => t.p.Normals.Select((n, i) =>
            {
                var r = Vector3.TransformNormal(new Vector3(n.X, n.Y, n.Z), rig.TrophyToRig);
                int vi = cornerV[pi][i];
                if (rig.BindRot != null && vi >= 0) r = Vector3.Transform(r, rig.BindRot[vi]);
                return r.LengthSquared > 1e-12f ? r.Normalized() : Vector3.UnitY;
            }).ToArray()).ToList();

            var pose = new SkeletonPose(sk.Count); var world = new Matrix4[sk.Count]; var skin = new Matrix4[sk.Count];
            var posed = new Vector3[bind.Length];
            // --legs <Clip>: play it on the legs layer (root..Spine_1) under --clip on the torso, as the game does (walking while
            // shooting); a Space Pirate's blade arm then keeps the legs clip while the torso fires (TrophyRigs.OffHandStaysDown)
            int legsClip = Arg("--legs", "") is string lg && lg != "" ? (int)Enum.Parse<PlayerAnimation>(lg, ignoreCase: true) : -1;
            var legsNodes = Enumerable.Range(0, sk.Count).Select(i => !sk.AboveSpine[i]).ToArray();
            var torsoNodes = Enumerable.Range(0, sk.Count).Select(i => sk.AboveSpine[i]).ToArray();
            var offArm = sk.Subtree("L_shoulder");
            bool offHand = TrophyRigs.OffHandStaysDown(trophy) && !args.Contains("--nooffhand");
            void Pose(int c, int f)
            {
                if (legsClip >= 0 && c == clip)
                {
                    int fl = f % Math.Max(1, dsModel.AnimationGroups.Node[legsClip].FrameCount);
                    DsSkeleton.Sample(dsModel, legsClip, fl, pose, legsNodes); DsSkeleton.Sample(dsModel, c, f, pose, torsoNodes);
                    if (offHand && TrophyRigs.IsFiringClip(c)) DsSkeleton.Sample(dsModel, legsClip, fl, pose, offArm);
                }
                else DsSkeleton.Sample(dsModel, c, f, pose);
                sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                HdRigTool.Deform(rig, bind, world, posed);
                for (int i = 0; i < sk.Count; i++) skin[i] = rig.InvBind[i] * world[i];
            }
            Vector3 TurnNormal(int v, Vector3 n)
            {
                Vector3 acc = Vector3.Zero;
                for (int k = 0; k < 4; k++) { float w = rig.Weights[v * 4 + k]; if (w > 0) acc += Vector3.TransformVector(n, skin[rig.Bones[v * 4 + k]]) * w; }
                return acc.LengthSquared > 1e-12f ? acc.Normalized() : n;
            }
            // framing: the standing Idle's height, feet on the floor at y = 0
            Pose((int)PlayerAnimation.Idle, 0);
            float top = posed.Max(p => p.Y), H = top - Math.Min(0f, posed.Min(p => p.Y));
            float half = 0.62f * H, cy = 0.5f * top;

            var nws = new NativeWindowSettings { ClientSize = new Vector2i(64, 64), StartVisible = false, APIVersion = new Version(4, 3), Profile = ContextProfile.Core, Title = "hdanim" };
            using var win = new NativeWindow(nws);
            win.MakeCurrent();
            int prog = HdLook.Link(GxShader.Vert, GxShader.Frag);
            int U(string n) => GL.GetUniformLocation(prog, n);
            int fbo = GL.GenFramebuffer(); GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            int col = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, col);
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 0, RenderbufferStorage.Rgba8, size, size);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, col);
            int dep = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, dep);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, dep);
            GL.Viewport(0, 0, size, size);
            GL.UseProgram(prog);
            GL.Uniform1(U("uSkin"), 0);
            GL.Uniform3(U("uSceneAmb"), amb, amb, amb);
            GL.Uniform1(U("uNumLights"), 1);
            var ld = new float[12]; var lc = new float[12]; var ls = new float[12]; var lk = new float[12];
            var L0 = new Vector3(0.35f, 0.55f, 1f).Normalized(); ld[0] = L0.X; ld[1] = L0.Y; ld[2] = L0.Z; lc[0] = lc[1] = lc[2] = 0.75f; lk[0] = 8f; lk[2] = 1f - 8f;
            GL.Uniform3(U("uLightDir"), 4, ld); GL.Uniform3(U("uLightCol"), 4, lc); GL.Uniform3(U("uLightSpecCol"), 4, ls); GL.Uniform3(U("uLightSpecK"), 4, lk);
            GL.Uniform1(U("uExposure"), 1f); GL.Uniform1(U("uRawEnvNormal"), 0);
            for (int t = 0; t < 8; t++) GL.Uniform1(U("uTex" + t), t);
            var texCache = new Dictionary<string, int>();
            int white = HdLook.MakeTex(new byte[] { 255, 255, 255, 255 }, 1, 1);
            int LoadTex(string? name)
            {
                if (name == null) return white;
                if (texCache.TryGetValue(name, out int id)) return id;
                string f = Path.Combine(dir, name + ".png");
                if (!File.Exists(f)) return texCache[name] = white;
                using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(f);
                var px = new byte[img.Width * img.Height * 4]; img.CopyPixelDataTo(px);
                if (formats.TryGetValue(name, out int fmt) && Mdl0Gx.IsIntensity(fmt))
                    for (int i = 0; i < px.Length; i += 4) px[i + 3] = px[i];
                return texCache[name] = HdLook.MakeTex(px, img.Width, img.Height);
            }
            int vao = GL.GenVertexArray(); GL.BindVertexArray(vao);
            int vbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);

            foreach (int f in frames)
            {
                Pose(clip, f);
                if (bindOnly) { Array.Copy(bind, posed, bind.Length); for (int i = 0; i < sk.Count; i++) skin[i] = Matrix4.Identity; }
                foreach (float yaw in yaws)
                {
                    float a = yaw * MathF.PI / 180f;
                    // the game's models face -Z in their own space (their right hand at +X): the front camera sits at -Z
                    var eye = new Vector3(-MathF.Sin(a) * 10f * H, cy, -MathF.Cos(a) * 10f * H);
                    var view = Matrix4.LookAt(eye, new Vector3(0, cy, 0), Vector3.UnitY);
                    var proj = Matrix4.CreateOrthographicOffCenter(-half, half, -half, half, 0.1f, 30f * H);
                    var mvp = view * proj;
                    GL.UniformMatrix4(U("uMvp"), false, ref mvp);
                    var vr = new Matrix3(view); GL.UniformMatrix3(U("uViewRot"), false, ref vr);
                    GL.ClearColor(0.11f, 0.11f, 0.12f, 1f);
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    GL.Enable(EnableCap.DepthTest);
                    foreach (bool blendPass in new[] { false, true })
                        for (int pi = 0; pi < parts.Count; pi++)
                        {
                            var (part, gm) = parts[pi];
                            if (gm == null || gm.BlendEnable != blendPass) continue;
                            var P = GxShader.Pack(gm);
                            HdLook.Upload(prog, P);
                            for (int u = 0; u < 8; u++)
                            {
                                GL.ActiveTexture(TextureUnit.Texture0 + u);
                                var Lr = P.Units[u];
                                GL.BindTexture(TextureTarget.Texture2D, LoadTex(Lr?.Name));
                                if (Lr != null) HdLook.Sampler(Lr);
                            }
                            GL.ActiveTexture(TextureUnit.Texture0);
                            if (P.Blend)
                            {
                                GL.Enable(EnableCap.Blend);
                                GL.BlendFunc(HdLook.SrcFactor(P.BlendSrc), HdLook.DstFactor(P.BlendDst));
                                GL.BlendEquation(P.BlendSubtract ? BlendEquationMode.FuncReverseSubtract : BlendEquationMode.FuncAdd);
                            }
                            else GL.Disable(EnableCap.Blend);
                            GL.DepthMask(P.DepthWrite && !P.Blend);
                            GL.DepthFunc((DepthFunction)((int)DepthFunction.Never + P.DepthFunc));
                            var cv = cornerV[pi]; var vtx = new List<float>(part.Positions.Count * 11);
                            for (int t = 0; t + 2 < part.Positions.Count; t += 3)
                            {
                                int va = cv[t], vb = cv[t + 1], vc = cv[t + 2];
                                if (va < 0 || vb < 0 || vc < 0 || rig.HidesTriangle(va, vb, vc)) continue;
                                for (int k = 0; k < 3; k++)
                                {
                                    int i = t + k, vi = cv[i];
                                    var q = posed[vi]; var n = TurnNormal(vi, cornerN[pi][i]); var uv = part.Uvs[i];
                                    var cl = part.Colors.Count > i ? part.Colors[i] : part.FlatColor;
                                    vtx.AddRange(new[] { q.X, q.Y, q.Z, n.X, n.Y, n.Z, uv.X, uv.Y, cl.X, cl.Y, GxShader.PackBlueAlpha(cl.Z, cl.W) });
                                }
                            }
                            var arr = vtx.ToArray();
                            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * 4, arr, BufferUsageHint.StreamDraw);
                            for (int at = 0; at < 4; at++) GL.EnableVertexAttribArray(at);
                            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 44, 0);
                            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 44, 12);
                            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 44, 24);
                            GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 44, 32);
                            GL.DrawArrays(PrimitiveType.Triangles, 0, arr.Length / 11);
                        }
                    GL.DepthMask(true);
                    var outPx = new byte[size * size * 4];
                    GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, outPx);
                    using var img = new Image<Rgba32>(size, size);
                    int floor = (int)MathF.Round((cy + half - 0f) / (2 * half) * size);   // image row of y = 0
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            int o = ((size - 1 - y) * size + x) * 4;
                            var px = new Rgba32(outPx[o], outPx[o + 1], outPx[o + 2], 255);
                            bool bgPx = outPx[o] == 28 && outPx[o + 1] == 28 && outPx[o + 2] == 31;
                            if (y == floor && (bgPx || x % 6 < 3)) px = new Rgba32(90, 200, 90, 255);
                            img[x, y] = px;
                        }
                    string tag = (bindOnly ? "Bind" : ((PlayerAnimation)clip).ToString()) + (legsClip >= 0 ? "+" + (PlayerAnimation)legsClip : "") + (args.Contains("--nooffhand") ? "_nooff" : "");
                    string file = Path.Combine(outDir, string.Create(ci, $"{trophy}_{key}_{tag}_{f}_y{yaw:0}.png"));
                    img.SaveAsPng(file);
                }
            }
            Console.WriteLine($"{trophy} '{key}': {frames.Count} frames of {(PlayerAnimation)clip} x {yaws.Length} views -> {outDir}");
        }
    }
}
