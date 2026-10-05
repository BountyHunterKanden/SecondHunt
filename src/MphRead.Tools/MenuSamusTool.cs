using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Assets;
using MphRecomp.Frontend;
using MphRecomp.Render;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MphRead
{
    // -femenusamus [suit] [--eye x,y,z] [--target x,y,z] [--fov deg] [--size w] [--out dir] [--tag t] [--hide/--opaque/--black mats] [--bind] [--native|--clip] [--face]: the menus' HD Samus backdrop
    // (Core Frontend/MenuSamus.cs) rendered on the PC exactly as the Odin does it -- the recipe's suit, clip, frame and
    // camera, CampaignSuit's lights, the GX program -- from brawl_extract/Converted/<suit>. Writes menu_samus.png (the
    // picture) and menu_samus_cmp.png (the DS art | ours | ours under the menus' green tint) to extract_out/frontend.
    // --eye/--target/--fov override the recipe, to tune it; the posed body's bounds are printed to frame by.
    internal static class MenuSamusTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            var ci = CultureInfo.InvariantCulture;
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            Vector3 V3(string s) { var p = s.Split(',').Select(x => float.Parse(x, ci)).ToArray(); return new Vector3(p[0], p[1], p[2]); }
            MenuSamus recipe = MenuSamus.Load();
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string converted = Path.Combine(repo, "brawl_extract", "Converted");
            string suit = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : recipe.PickSuit(converted) ?? "";
            if (suit == "") { Console.WriteLine("no suit of the recipe in " + converted); return; }
            Vector3 eye = Arg("--eye", "") is string e && e != "" ? V3(e) : recipe.Eye;
            Vector3 target = Arg("--target", "") is string t0 && t0 != "" ? V3(t0) : recipe.Target;
            float fov = float.Parse(Arg("--fov", recipe.FovY.ToString(ci)), ci);
            int w = int.Parse(Arg("--size", "512")), h = (int)MathF.Round(w / MenuSamus.Aspect);
            string outDir = Arg("--out", FrontendTool.OutDir());
            Directory.CreateDirectory(outDir);

            string dir = Path.Combine(converted, suit);
            string dae = Directory.GetFiles(dir, "*.dae").First();
            Model dsModel = Read.GetModelInstance(Metadata.HunterModels[Hunter.Samus][0]).Model;
            var sk = new DsSkeleton(dsModel);
            // the rig the Odin picks (CampaignSuit.PickRig): the pose file's, else pieces / retro / rigid, else sculpted
            string rigLabel = "sculpted";
            TrophyRig? rig = File.Exists(TrophyRigIO.PosedPathFor(dae)) ? TrophyRigIO.LoadPosedRig(dae, sk.Names) : null;
            if (rig != null) rigLabel = "posed";
            foreach (string key in new[] { "pieces", "retro", "rigid" })
            {
                if (rig != null) break;
                var v = Array.Find(TrophyRigIO.Variants, x => x.Key == key);
                if (v == null || !File.Exists(TrophyRigIO.VariantPathFor(dae, v))) continue;
                rig = TrophyRigIO.LoadOrFitVariant(dae, dsModel, Array.Empty<Vector3>(), v, fit: false);
                rigLabel = key;
            }
            rig ??= TrophyRigIO.Load(Path.ChangeExtension(dae, ".mphrig"), TrophyRigIO.CacheKey(dae))
                ?? throw new InvalidOperationException($"{suit}: no rig cache");
            Console.WriteLine($"{suit}: rig {rigLabel}");
            TrophyFeet.Settle(rig, dsModel, sk, _ => { });
            var model = DaeModel.Load(dae);
            var mats = new Dictionary<string, GxMaterial>();
            foreach (var m in GxJson.Read(GxJson.PathFor(dae))) mats.TryAdd(m.Name, m);
            // --hide / --opaque / --black mat4,mat5: per-material trials (a name matches whole or as its "_matN" end)
            string[] Names(string k) => Arg(k, "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            bool Hit(string[] list, string name) => list.Any(n => name == n || name.EndsWith("_" + n));
            // the recipe's visor (face hidden, pane opaque) unless --hide/--opaque say otherwise; --face draws the model as is
            bool face = args.Contains("--face");
            string[] hide = Arg("--hide", "") != "" ? Names("--hide") : face ? Array.Empty<string>() : recipe.HideFor(suit);
            string[] opaque = Arg("--opaque", "") != "" ? Names("--opaque") : face ? Array.Empty<string>() : recipe.OpaqueFor(suit);
            string[] black = Names("--black");
            foreach (var m in mats.Values.Where(m => Hit(opaque, m.Name))) { m.BlendEnable = false; m.DepthWrite = true; }

            // --native (the recipe's "pose": "built"; --clip = the DS clip instead): the model as Retro built it (the rig's corners
            // through TrophyToRig, not a straightened BindPos); implies --bind
            bool native = args.Contains("--native") || (recipe.AsBuilt && !args.Contains("--clip"));
            var bind = (native ? null : rig.BindPos) ?? rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
            var parts = model.Meshes.Select(p => (p, g: mats.TryGetValue(p.Material, out var gm) ? gm : null)).ToList();
            var cornerV = parts.Select(t => t.p.Positions.Select(q => rig.IndexOf(new System.Numerics.Vector3(q.X, q.Y, q.Z))).ToArray()).ToList();
            var cornerN = parts.Select((t, pi) => t.p.Normals.Select((n, i) =>
            {
                var r = Vector3.TransformNormal(new Vector3(n.X, n.Y, n.Z), rig.TrophyToRig);
                int vi = cornerV[pi][i];
                if (!native && rig.BindRot != null && vi >= 0) r = Vector3.Transform(r, rig.BindRot[vi]);
                return r.LengthSquared > 1e-12f ? r.Normalized() : Vector3.UnitY;
            }).ToArray()).ToList();
            var pose = new SkeletonPose(sk.Count);
            var world = new Matrix4[sk.Count];
            var skin = new Matrix4[sk.Count];
            var posed = new Vector3[bind.Length];
            DsSkeleton.Sample(dsModel, recipe.Clip, recipe.Frame, pose);
            sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
            HdRigTool.Deform(rig, bind, world, posed);
            for (int i = 0; i < sk.Count; i++) skin[i] = rig.InvBind[i] * world[i];
            // --bind: the model as it was built (its own bind pose, straight on), no DS clip
            bool bindPose = native || args.Contains("--bind");
            if (bindPose) Array.Copy(bind, posed, bind.Length);
            Vector3 TurnNormal(int v, Vector3 n)
            {
                if (bindPose) return n;
                Vector3 acc = Vector3.Zero;
                for (int k = 0; k < 4; k++) { float wt = rig.Weights[v * 4 + k]; if (wt > 0) acc += Vector3.TransformVector(n, skin[rig.Bones[v * 4 + k]]) * wt; }
                return acc.LengthSquared > 1e-12f ? acc.Normalized() : n;
            }
            Vector3 lo = new(posed.Min(p => p.X), posed.Min(p => p.Y), posed.Min(p => p.Z));
            Vector3 hi = new(posed.Max(p => p.X), posed.Max(p => p.Y), posed.Max(p => p.Z));
            Console.WriteLine($"{suit}: posed bounds {lo} .. {hi}; camera eye {eye} target {target} fov {fov}");

            var nws = new NativeWindowSettings { ClientSize = new Vector2i(64, 64), StartVisible = false, APIVersion = new Version(4, 3), Profile = ContextProfile.Core, Title = "menusamus" };
            using var win = new NativeWindow(nws);
            win.MakeCurrent();
            int prog = HdLook.Link(GxShader.Vert, GxShader.Frag);
            int U(string n) => GL.GetUniformLocation(prog, n);
            int fbo = GL.GenFramebuffer(); GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            int col = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, col);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, col);
            int dep = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, dep);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, dep);
            GL.Viewport(0, 0, w, h);
            GL.UseProgram(prog);
            // CampaignSuit's lights (the campaign's HD suits), so the PC picture is the device's
            GL.Uniform1(U("uSkin"), 0);
            GL.Uniform3(U("uSceneAmb"), 102f / 255f, 100f / 255f, 100f / 255f);
            GL.Uniform1(U("uNumLights"), 1);
            var L0 = new Vector3(-50f, 50f, 90f).Normalized();
            float[] ld = { L0.X, L0.Y, L0.Z, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, lc = { 0.4f, 0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            float[] ls = { 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, lk = { 64, 0, -63, 1, 0, 0, 1, 0, 0, 1, 0, 0 };
            GL.Uniform3(U("uLightDir"), 4, ld); GL.Uniform3(U("uLightCol"), 4, lc); GL.Uniform3(U("uLightSpecCol"), 4, ls); GL.Uniform3(U("uLightSpecK"), 4, lk);
            GL.Uniform1(U("uExposure"), 1f); GL.Uniform1(U("uRawEnvNormal"), 0);
            for (int t = 0; t < 8; t++) GL.Uniform1(U("uTex" + t), t);
            var texCache = new Dictionary<string, int>();
            int white = HdLook.MakeTex(new byte[] { 255, 255, 255, 255 }, 1, 1);
            int blackTex = HdLook.MakeTex(new byte[] { 0, 0, 0, 255 }, 1, 1);
            int LoadTex(string? name)
            {
                if (name == null) return white;
                if (texCache.TryGetValue(name, out int id)) return id;
                string f = Path.Combine(dir, name + ".png");
                if (!File.Exists(f)) return texCache[name] = white;
                using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(f);
                var px = new byte[img.Width * img.Height * 4];
                img.CopyPixelDataTo(px);
                return texCache[name] = HdLook.MakeTex(px, img.Width, img.Height);
            }
            int vao = GL.GenVertexArray(); GL.BindVertexArray(vao);
            int vbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            var view = Matrix4.LookAt(eye, target, Vector3.UnitY);
            var proj = Matrix4.CreatePerspectiveFieldOfView(fov * MathF.PI / 180f, MenuSamus.Aspect, 0.05f, 100f);
            var mvp = view * proj;
            GL.UniformMatrix4(U("uMvp"), false, ref mvp);
            var vr = new Matrix3(view);
            GL.UniformMatrix3(U("uViewRot"), false, ref vr);
            GL.ClearColor(0, 0, 0, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);
            foreach (bool blendPass in new[] { false, true })
                for (int pi = 0; pi < parts.Count; pi++)
                {
                    var (part, gm) = parts[pi];
                    if (gm == null || gm.BlendEnable != blendPass || Hit(hide, gm.Name)) continue;
                    var P = GxShader.Pack(gm);
                    HdLook.Upload(prog, P);
                    bool dark = Hit(black, gm.Name);
                    for (int u = 0; u < 8; u++)
                    {
                        GL.ActiveTexture(TextureUnit.Texture0 + u);
                        var Lr = P.Units[u];
                        GL.BindTexture(TextureTarget.Texture2D, dark ? blackTex : LoadTex(Lr?.Name));
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
                    var cv = cornerV[pi];
                    var vtx = new List<float>(part.Positions.Count * 11);
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
            var outPx = new byte[w * h * 4];
            GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, outPx);
            using var ours = new Image<Rgba32>(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = ((h - 1 - y) * w + x) * 4;
                    ours[x, y] = new Rgba32(outPx[o], outPx[o + 1], outPx[o + 2], 255);
                }
            string tag = Arg("--tag", "") is string tg && tg != "" ? "_" + tg : "";
            ours.SaveAsPng(Path.Combine(outDir, $"menu_samus{tag}.png"));

            // the DS art | ours | ours under the menus' green tint (21/32 of (0, 31, 0)/31, FrontendSession.Tint)
            using var cmp = new Image<Rgba32>(w * 3 + 8, h, new Rgba32(40, 40, 40, 255));
            using (Image<Rgba32> ds = DsArt())
            {
                ds.Mutate(c => c.Resize(w, h, KnownResamplers.NearestNeighbor));
                cmp.Mutate(c => c.DrawImage(ds, new Point(0, 0), 1f).DrawImage(ours, new Point(w + 4, 0), 1f));
            }
            using (Image<Rgba32> tinted = ours.Clone())
            {
                tinted.ProcessPixelRows(a =>
                {
                    for (int y = 0; y < a.Height; y++)
                        foreach (ref Rgba32 p in a.GetRowSpan(y))
                            p = new Rgba32((byte)(p.R * 11 / 32), (byte)((p.G * 11 + 255 * 21) / 32), (byte)(p.B * 11 / 32), 255);
                });
                cmp.Mutate(c => c.DrawImage(tinted, new Point(2 * (w + 4), 0), 1f));
            }
            string cmpPath = Path.Combine(outDir, $"menu_samus_cmp{tag}.png");
            cmp.SaveAsPng(cmpPath);
            Console.WriteLine("-> " + cmpPath);
        }

        // sourceimages/bg main1 (top) over main2 (touch screen), as FrontendSession draws them
        private static Image<Rgba32> DsArt()
        {
            var img = new Image<Rgba32>(256, 384);
            for (int s = 0; s < 2; s++)
            {
                (int bw, int bh, byte[] rgba) = FrontendSession.LoadBitmap(Path.Combine(Paths.FileSystem, "sourceimages", "bg", s == 0 ? "main1.bin" : "main2.bin"));
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        int o = (y * bw + x) * 4;
                        img[x, y + s * 192] = new Rgba32(rgba[o], rgba[o + 1], rgba[o + 2], 255);
                    }
            }
            return img;
        }
    }
}
