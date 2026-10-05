using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    // A suit's HD morph ball as the viewer draws it: HdBall.For finds hd/<suit>Ball, HdBall.Placement puts it on the floor at
    // MPH's size, spun and turned; the lit core (HdBall.CoreMaterial / CoreCorners) shows through the gap -- all through the
    // same GX program the Odin runs, seen from the front-right, one picture per spin.
    //   MphRead.Tools.dll -hdball <suit> [--spins 0,0.8,1.6,2.4] [--heading 0] [--size 360] [--out dir]
    internal static class HdBallLook
    {
        public static void Run(string[] args)
        {
            string suit = args[1];
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string outDir = Arg("--out", Directory.GetCurrentDirectory()); Directory.CreateDirectory(outDir);
            int size = int.Parse(Arg("--size", "360"));
            var spins = Arg("--spins", "0,0.8,1.6,2.4").Split(',').Select(s => float.Parse(s, ci)).ToArray();
            float heading = float.Parse(Arg("--heading", "0"), ci);
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string conv = Path.Combine(repo, "brawl_extract", "Converted");
            var info = HdBall.For(conv, suit);
            if (info == null) { Console.WriteLine($"{suit}: no ball folder {HdBall.IdFor(suit)}"); return; }
            var model = DaeModel.Load(info.DaePath);
            var mats = new Dictionary<string, GxMaterial>();
            foreach (var m in GxJson.Read(GxJson.PathFor(info.DaePath))) mats.TryAdd(m.Name, m);
            var center = new Vector3((model.Min.X + model.Max.X) / 2, (model.Min.Y + model.Max.Y) / 2, (model.Min.Z + model.Max.Z) / 2);
            var sz = model.Max - model.Min; float width = Math.Max(sz.X, Math.Max(sz.Y, sz.Z));

            var nws = new NativeWindowSettings { ClientSize = new Vector2i(64, 64), StartVisible = false, APIVersion = new Version(4, 3), Profile = ContextProfile.Core, Title = "hdball" };
            using var win = new NativeWindow(nws);
            win.MakeCurrent();
            int prog = HdLook.Link(GxShader.Vert, GxShader.Frag);
            int U(string n) => GL.GetUniformLocation(prog, n);
            int fbo = GL.GenFramebuffer(); GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            int col = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, col);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, size, size);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, col);
            int dep = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, dep);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, dep);
            GL.Viewport(0, 0, size, size); GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal);
            GL.UseProgram(prog);
            // the camera: front-right and a little above, looking at the ball's middle (the ball rests on y = 0, 1 unit wide)
            var view = Matrix4.LookAt(new Vector3(1.3f, 1.1f, -2.2f), new Vector3(0, 0.5f, 0), Vector3.UnitY);
            var mvp = view * Matrix4.CreateOrthographicOffCenter(-0.8f, 0.8f, -0.8f, 0.8f, 0.1f, 20f);
            GL.UniformMatrix4(U("uMvp"), false, ref mvp); var vr = new Matrix3(view); GL.UniformMatrix3(U("uViewRot"), false, ref vr);
            GL.Uniform1(U("uSkin"), 0); GL.Uniform3(U("uSceneAmb"), 0.4f, 0.392f, 0.392f); GL.Uniform1(U("uNumLights"), 1);
            var L0 = new Vector3(-0.44f, 0.44f, 0.79f).Normalized();
            GL.Uniform3(U("uLightDir"), 4, new[] { L0.X, L0.Y, L0.Z, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            GL.Uniform3(U("uLightCol"), 4, new[] { 0.4f, 0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            GL.Uniform3(U("uLightSpecCol"), 4, new[] { 1f, 1f, 1f, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            GL.Uniform3(U("uLightSpecK"), 4, new[] { 64f, 0f, -63f, 1, 0, 0, 1, 0, 0, 1, 0, 0 });
            GL.Uniform1(U("uExposure"), 1f); GL.Uniform1(U("uRawEnvNormal"), 0);
            for (int t = 0; t < 8; t++) GL.Uniform1(U("uTex" + t), t);
            var texCache = new Dictionary<string, int>(); int white = HdLook.MakeTex(new byte[] { 255, 255, 255, 255 }, 1, 1);
            int LoadTex(string? name)
            {
                if (name == null) return white;
                if (texCache.TryGetValue(name, out int id)) return id;
                string f = Path.Combine(info.Folder, name + ".png");
                if (!File.Exists(f)) return texCache[name] = white;
                using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(f);
                var px = new byte[img.Width * img.Height * 4]; img.CopyPixelDataTo(px);
                return texCache[name] = HdLook.MakeTex(px, img.Width, img.Height);
            }
            int vao = GL.GenVertexArray(); GL.BindVertexArray(vao); int vbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            void Draw(GxMaterial gm, List<(Vector3 P, Vector3 N, Vector2 Uv)> corners, Matrix4 M)
            {
                var P = GxShader.Pack(gm); HdLook.Upload(prog, P);
                for (int u = 0; u < 8; u++) { GL.ActiveTexture(TextureUnit.Texture0 + u); var L = P.Units[u]; GL.BindTexture(TextureTarget.Texture2D, LoadTex(L?.Name)); if (L != null) HdLook.Sampler(L); }
                GL.ActiveTexture(TextureUnit.Texture0);
                var v = new float[corners.Count * 11];
                for (int i = 0; i < corners.Count; i++)
                {
                    var q = Vector3.TransformPosition(corners[i].P, M); var n = Vector3.TransformNormal(corners[i].N, M).Normalized();
                    int o = i * 11; v[o] = q.X; v[o + 1] = q.Y; v[o + 2] = q.Z; v[o + 3] = n.X; v[o + 4] = n.Y; v[o + 5] = n.Z;
                    v[o + 6] = corners[i].Uv.X; v[o + 7] = corners[i].Uv.Y; v[o + 8] = 1; v[o + 9] = 1; v[o + 10] = GxShader.PackBlueAlpha(1, 1);
                }
                GL.BufferData(BufferTarget.ArrayBuffer, v.Length * 4, v, BufferUsageHint.StreamDraw);
                for (int a = 0; a < 4; a++) GL.EnableVertexAttribArray(a);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 44, 0); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 44, 12);
                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 44, 24); GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 44, 32);
                GL.DrawArrays(PrimitiveType.Triangles, 0, corners.Count);
            }
            var shell = model.Meshes.Select(p => (p, g: mats.TryGetValue(p.Material, out var gm) ? gm : null)).Where(t => t.g != null).ToList();
            var core = info.Core is System.Numerics.Vector3 c ? HdBall.CoreCorners(new System.Numerics.Vector3(center.X, center.Y, center.Z), width)
                .Select(x => (new Vector3(x.Pos.X, x.Pos.Y, x.Pos.Z), new Vector3(x.Normal.X, x.Normal.Y, x.Normal.Z), Vector2.Zero)).ToList() : null;
            using var strip = new Image<Rgba32>(size * spins.Length, size);
            for (int si = 0; si < spins.Length; si++)
            {
                GL.ClearColor(0.16f, 0.16f, 0.17f, 1f); GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                var M = HdBall.Placement(center, width, spins[si], heading);
                if (core != null) Draw(HdBall.CoreMaterial(info.Core!.Value), core, M);
                foreach (var (part, gm) in shell)
                    Draw(gm!, Enumerable.Range(0, part.Positions.Count).Select(i => (new Vector3(part.Positions[i].X, part.Positions[i].Y, part.Positions[i].Z),
                        new Vector3(part.Normals[i].X, part.Normals[i].Y, part.Normals[i].Z), new Vector2(part.Uvs[i].X, part.Uvs[i].Y))).ToList(), M);
                var px = new byte[size * size * 4]; GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, px);
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { int o = ((size - 1 - y) * size + x) * 4; strip[si * size + x, y] = new Rgba32(px[o], px[o + 1], px[o + 2], 255); }
            }
            string file = Path.Combine(outDir, suit + "_ball.png"); strip.SaveAsPng(file);
            Console.WriteLine($"{suit}: {info.Id}, {shell.Count} parts, core {(info.Core != null ? "lit" : "none")}, {spins.Length} spins -> {file}");
        }
    }
}
