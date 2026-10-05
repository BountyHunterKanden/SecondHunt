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
    // Render a Brawl trophy on the PC through the SAME GX program the Odin uses (MphRecomp.Render.GxShader), straight
    // on from the front, orthographic, over a flat background -- the conditions of the BrawlCrate reference renders
    // (scratchpad brawl_render2.ps1) -- so the two can be compared pixel for pixel.
    //   MphRead.Tools.dll -hdlook <Trophy> [--out dir] [--size 1024] [--amb 1] [--light x,y,z,r,g,b] [--bg 255,0,255] [--time s]
    // Writes <out>/<Trophy>_ours.png. Lights are in camera space (x right, y up, z towards the viewer).
    internal static class HdLook
    {
        static readonly string Converted = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "brawl_extract", "Converted"));

        public static void Run(string[] args)
        {
            string trophy = args[1];
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string outDir = Arg("--out", Directory.GetCurrentDirectory());
            int size = int.Parse(Arg("--size", "1024"));
            var ambRgb = Arg("--amb", "1").Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (ambRgb.Length == 1) ambRgb = new[] { ambRgb[0], ambRgb[0], ambRgb[0] };
            var bg = Arg("--bg", "255,0,255").Split(',').Select(x => int.Parse(x) / 255f).ToArray();
            var lights = args.Select((a, i) => (a, i)).Where(t => t.a == "--light" && t.i + 1 < args.Length)
                .Select(t => t.i + 1).Select(i => args[i].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
            Directory.CreateDirectory(outDir);

            string dir = Path.Combine(Converted, trophy);
            string dae = Directory.GetFiles(dir, "*.dae").First();
            // the GX programs: the .brres, or <dae>.gx.json for a model that has none (a Retro suit)
            string? brres = Directory.GetFiles(dir, "*.brres").FirstOrDefault();
            var model = DaeModel.Load(dae);
            var mats = new Dictionary<string, GxMaterial>();
            foreach (var m in brres != null ? Mdl0Gx.Read(brres) : GxJson.Read(GxJson.PathFor(dae))) mats.TryAdd(m.Name, m);
            var formats = brres != null ? Mdl0Gx.TextureFormats(brres) : new Dictionary<string, int>();
            double seconds = double.Parse(Arg("--time", "0"), System.Globalization.CultureInfo.InvariantCulture);   // where scrolling layers are

            var nws = new NativeWindowSettings { ClientSize = new Vector2i(64, 64), StartVisible = false, APIVersion = new Version(4, 3), Profile = ContextProfile.Core, Title = "hdlook" };
            using var win = new NativeWindow(nws);
            win.MakeCurrent();

            int prog = Link(GxShader.Vert, GxShader.Frag);
            int U(string n) => GL.GetUniformLocation(prog, n);

            // framebuffer
            int fbo = GL.GenFramebuffer(); GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            int col = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, col);
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 0, RenderbufferStorage.Rgba8, size, size);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, col);
            int dep = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, dep);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, dep);
            GL.Viewport(0, 0, size, size);
            GL.ClearColor(bg[0], bg[1], bg[2], 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest); GL.DepthFunc(args.Contains("--depthless") ? DepthFunction.Less : DepthFunction.Lequal);

            // camera: orthographic, looking down -Z at the model's bounding box (front view), a little margin
            var mn = model.Min; var mx = model.Max; var c = (mn + mx) * 0.5f;
            float half = Math.Max(mx.X - mn.X, mx.Y - mn.Y) * 0.55f, depth = (mx.Z - mn.Z) + 10f;
            var view = Matrix4.LookAt(new Vector3(c.X, c.Y, mx.Z + 5f), new Vector3(c.X, c.Y, c.Z), Vector3.UnitY);
            var proj = Matrix4.CreateOrthographicOffCenter(-half, half, -half, half, 0.1f, depth + 10f);
            // --cam <file>: BrawlCrate's camera (brawl_render2.ps1 writes "x,y,z,1 fovY near far ortho"): a perspective view
            // from that point looking down -Z, as its viewer frames the model
            string camFile = Arg("--cam", "");
            if (camFile != "" && File.Exists(camFile))
            {
                var t = File.ReadAllText(camFile).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var cp = t[0].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                float fov = float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture), near = float.Parse(t[2], System.Globalization.CultureInfo.InvariantCulture);
                var eye = new Vector3(cp[0], cp[1], cp[2]);
                view = Matrix4.LookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
                proj = Matrix4.CreatePerspectiveFieldOfView(fov * MathF.PI / 180f, 1f, near, 2000f);
            }
            var mvp = view * proj;   // OpenTK: row vectors
            GL.UseProgram(prog);
            GL.UniformMatrix4(U("uMvp"), false, ref mvp);
            var vr = new Matrix3(view); GL.UniformMatrix3(U("uViewRot"), false, ref vr);
            GL.Uniform1(U("uSkin"), 0);
            GL.Uniform3(U("uSceneAmb"), ambRgb[0], ambRgb[1], ambRgb[2]);
            GL.Uniform1(U("uNumLights"), lights.Count);
            if (lights.Count > 0)
            {
                var ld = new float[12]; var lc = new float[12]; var ls = new float[12]; var lk = new float[12];
                for (int i = 0; i < Math.Min(4, lights.Count); i++)
                {
                    var L = lights[i]; var d = new Vector3(L[0], L[1], L[2]).Normalized();
                    ld[i * 3] = d.X; ld[i * 3 + 1] = d.Y; ld[i * 3 + 2] = d.Z;
                    lc[i * 3] = L[3]; lc[i * 3 + 1] = L[4]; lc[i * 3 + 2] = L[5];
                    float sp = L.Length > 6 ? L[6] : 0f, shin = L.Length > 7 ? L[7] : 16f;
                    ls[i * 3] = ls[i * 3 + 1] = ls[i * 3 + 2] = sp;
                    lk[i * 3] = 0.5f * shin; lk[i * 3 + 1] = 0f; lk[i * 3 + 2] = 1f - 0.5f * shin;
                }
                GL.Uniform3(U("uLightDir"), 4, ld); GL.Uniform3(U("uLightCol"), 4, lc); GL.Uniform3(U("uLightSpecCol"), 4, ls); GL.Uniform3(U("uLightSpecK"), 4, lk);
            }
            GL.Uniform1(U("uExposure"), 1f); GL.Uniform1(U("uRawEnvNormal"), args.Contains("--rawnrm") ? 1 : 0);
            for (int t = 0; t < 8; t++) GL.Uniform1(U("uTex" + t), t);

            var texCache = new Dictionary<string, int>();
            int white = MakeTex(new byte[] { 255, 255, 255, 255 }, 1, 1);
            int LoadTex(string? name)
            {
                if (name == null) return white;
                if (texCache.TryGetValue(name, out int id)) return id;
                string f = Path.Combine(dir, name + ".png");
                if (!File.Exists(f)) { Console.WriteLine($"  texture {name}.png MISSING"); return texCache[name] = white; }
                using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(f);
                var px = new byte[img.Width * img.Height * 4]; img.CopyPixelDataTo(px);
                if (!args.Contains("--noia") && formats.TryGetValue(name, out int fmt) && Mdl0Gx.IsIntensity(fmt))
                    for (int i = 0; i < px.Length; i += 4) px[i + 3] = px[i];   // I4/I8: intensity in all four channels
                return texCache[name] = MakeTex(px, img.Width, img.Height);
            }

            int vao = GL.GenVertexArray(); GL.BindVertexArray(vao);
            int vbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            // opaque parts first, then the blended ones (no depth write), as the device does
            var parts = model.Meshes.Select(p => (p, g: mats.TryGetValue(p.Material, out var gm) ? gm : null)).ToList();
            int missing = parts.Count(t => t.g == null);
            if (missing > 0) Console.WriteLine($"  {missing} parts have no GX material: " + string.Join(", ", parts.Where(t => t.g == null).Select(t => t.p.Material).Distinct()));
            foreach (bool blendPass in new[] { false, true })
                foreach (var (part, gm) in parts)
                {
                    if (gm == null || gm.BlendEnable != blendPass) continue;
                    var P = GxShader.Pack(gm);
                    if (P.Scrolls) P.TexMtx = GxShader.ScrolledTexMtx(gm, P, seconds);
                    Upload(prog, P);
                    for (int u = 0; u < 8; u++)
                    {
                        GL.ActiveTexture(TextureUnit.Texture0 + u);
                        var L = P.Units[u];
                        int tex = LoadTex(L?.Name);
                        GL.BindTexture(TextureTarget.Texture2D, tex);
                        if (L != null) Sampler(L);
                    }
                    GL.ActiveTexture(TextureUnit.Texture0);
                    if (P.Blend)
                    {
                        GL.Enable(EnableCap.Blend);
                        GL.BlendFunc(SrcFactor(P.BlendSrc), DstFactor(P.BlendDst));
                        GL.BlendEquation(P.BlendSubtract ? BlendEquationMode.FuncReverseSubtract : BlendEquationMode.FuncAdd);
                    }
                    else GL.Disable(EnableCap.Blend);
                    GL.DepthMask(P.DepthWrite && !P.Blend);
                    // the material's own depth test (as the app now does); --depthless = the old GL default <
                    GL.DepthFunc(args.Contains("--depthless") ? DepthFunction.Less : (DepthFunction)((int)DepthFunction.Never + P.DepthFunc));
                    var v = new float[part.Positions.Count * 11];
                    for (int i = 0; i < part.Positions.Count; i++)
                    {
                        var q = part.Positions[i]; var n = part.Normals[i]; var uv = part.Uvs[i]; var cl = part.Colors.Count > i ? part.Colors[i] : part.FlatColor;
                        int o = i * 11;
                        v[o] = q.X; v[o + 1] = q.Y; v[o + 2] = q.Z; v[o + 3] = n.X; v[o + 4] = n.Y; v[o + 5] = n.Z;
                        v[o + 6] = uv.X; v[o + 7] = uv.Y; v[o + 8] = cl.X; v[o + 9] = cl.Y; v[o + 10] = GxShader.PackBlueAlpha(cl.Z, cl.W);
                    }
                    GL.BufferData(BufferTarget.ArrayBuffer, v.Length * 4, v, BufferUsageHint.StreamDraw);
                    for (int a = 0; a < 4; a++) GL.EnableVertexAttribArray(a);
                    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 44, 0);
                    GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 44, 12);
                    GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 44, 24);
                    GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 44, 32);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, part.Positions.Count);
                }
            GL.DepthMask(true);
            var err = GL.GetError(); if (err != ErrorCode.NoError) Console.WriteLine("  GL error " + err);

            var outPx = new byte[size * size * 4];
            GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, outPx);
            using var outImg = new Image<Rgba32>(size, size);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int o = ((size - 1 - y) * size + x) * 4;
                    outImg[x, y] = new Rgba32(outPx[o], outPx[o + 1], outPx[o + 2], 255);
                }
            string outFile = Path.Combine(outDir, trophy + "_ours.png");
            outImg.SaveAsPng(outFile);
            Console.WriteLine($"{trophy}: {parts.Count} parts, {mats.Count} GX materials -> {outFile}");
        }

        internal static BlendingFactor SrcFactor(int f) => f switch
        {
            0 => BlendingFactor.Zero, 1 => BlendingFactor.One, 2 => BlendingFactor.DstColor, 3 => BlendingFactor.OneMinusDstColor,
            4 => BlendingFactor.SrcAlpha, 5 => BlendingFactor.OneMinusSrcAlpha, 6 => BlendingFactor.DstAlpha, _ => BlendingFactor.OneMinusDstAlpha,
        };
        internal static BlendingFactor DstFactor(int f) => f switch
        {
            0 => BlendingFactor.Zero, 1 => BlendingFactor.One, 2 => BlendingFactor.SrcColor, 3 => BlendingFactor.OneMinusSrcColor,
            4 => BlendingFactor.SrcAlpha, 5 => BlendingFactor.OneMinusSrcAlpha, 6 => BlendingFactor.DstAlpha, _ => BlendingFactor.OneMinusDstAlpha,
        };

        internal static void Sampler(GxLayer L)
        {
            TextureWrapMode W(int w) => w == 0 ? TextureWrapMode.ClampToEdge : w == 2 ? TextureWrapMode.MirroredRepeat : TextureWrapMode.Repeat;
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)W(L.WrapS));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)W(L.WrapT));
            var min = L.MinFilter switch
            {
                0 => TextureMinFilter.Nearest, 1 => TextureMinFilter.Linear, 2 => TextureMinFilter.NearestMipmapNearest,
                3 => TextureMinFilter.LinearMipmapNearest, 4 => TextureMinFilter.NearestMipmapLinear, _ => TextureMinFilter.LinearMipmapLinear,
            };
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)min);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)(L.MagFilter == 0 ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureLodBias, L.LodBias);
        }

        internal static int MakeTex(byte[] px, int w, int h)
        {
            int id = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, id);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            return id;
        }

        internal static void Upload(int prog, GxShader.Params p)
        {
            int U(string n) => GL.GetUniformLocation(prog, n);
            GL.Uniform1(U("uNumStages"), p.NumStages);
            GL.Uniform4(U("uCabcd"), 8, p.Cabcd); GL.Uniform4(U("uCmod"), 8, p.Cmod);
            GL.Uniform4(U("uAabcd"), 8, p.Aabcd); GL.Uniform4(U("uAmod"), 8, p.Amod);
            GL.Uniform4(U("uDst"), 8, p.Dst); GL.Uniform4(U("uKsel"), 8, p.Ksel);
            GL.Uniform1(U("uRasChan"), 8, p.RasChan); GL.Uniform4(U("uSwap"), 4, p.Swap);
            GL.Uniform4(U("uReg"), 4, p.Reg); GL.Uniform4(U("uKonst"), 4, p.Konst);
            GL.Uniform4(U("uColCtrl"), 2, p.ColCtrl); GL.Uniform4(U("uAlpCtrl"), 2, p.AlpCtrl); GL.Uniform2(U("uAttn"), 2, p.Attn);
            GL.Uniform4(U("uChanMat"), 2, p.ChanMat); GL.Uniform4(U("uChanAmb"), 2, p.ChanAmb);
            GL.Uniform1(U("uTgMode"), 8, p.TgMode); GL.UniformMatrix3(U("uTexMtx"), 8, false, p.TexMtx);
            GL.Uniform4(U("uAlphaTest"), 1, p.AlphaTest); GL.Uniform2(U("uAlphaRef"), 1, p.AlphaRef);
            GL.Uniform1(U("uBlendAlpha"), p.Blend ? 1 : 0);
            GL.Uniform1(U("uNmap"), p.NmapUnit + 1); GL.Uniform1(U("uNmapTc"), p.NmapCoord);
        }

        internal static int Link(string vs, string fs)
        {
            int Compile(ShaderType t, string src)
            {
                int s = GL.CreateShader(t); GL.ShaderSource(s, src); GL.CompileShader(s);
                GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
                if (ok == 0) throw new Exception($"{t} compile: {GL.GetShaderInfoLog(s)}");
                return s;
            }
            int p = GL.CreateProgram();
            GL.AttachShader(p, Compile(ShaderType.VertexShader, vs)); GL.AttachShader(p, Compile(ShaderType.FragmentShader, fs));
            GL.LinkProgram(p); GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int lk);
            if (lk == 0) throw new Exception("link: " + GL.GetProgramInfoLog(p));
            return p;
        }
    }
}
