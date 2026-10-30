using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Assets;
using MphRecomp.Campaign;
using MphRecomp.Render;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // -suitspec [suit|all]: the HD suit's draw as the Odin did it before owner queue #37's fix (every material on the GX
    // interpreter, GxShader.Vert, colour + depth together, pass 4 rebuilt through the interpreter) against now
    // (CampaignSuit: GunPrograms.Vert everywhere, each material on GunPrograms.SuitFrag where ArenaGxSpec writes it, the
    // opaque parts' depth first then their colour at <=, pass 4 depth only), offscreen on the PC. Per suit: the body in
    // the menus' recipe pose (CPU-posed, uSkin 0), three cameras (in front, grazing her hip as the Celestial Archives
    // landing camseq does, inside her torso) x alpha 1 and the unmorph fade's 0.5 (DrawFaded). Pixels differing by > 2
    // in any channel are counted; any -> extract_out/campaign/suitspec_<suit>.png (old | new | magenta diffs). The balls
    // (<suit>Ball) have every material's program compiled.
    internal static class CampaignSuitSpec
    {
        private const int W = 480, H = 360;

        private sealed class Batch
        {
            public int Start, Count, Pass, Prog;
            public GxMaterial Gx = null!;
            public GxShader.Params P = null!;
            public bool DepthFirst => Pass == 0 && P.AlphaTest[3] != 1;
        }

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string converted = Path.Combine(repo, "brawl_extract", "Converted");
            string outDir = Path.Combine(repo, "extract_out", "campaign");
            Directory.CreateDirectory(outDir);
            string which = args.Length >= 2 ? args[1] : "all";
            var nws = new NativeWindowSettings { ClientSize = new Vector2i(64, 64), StartVisible = false, APIVersion = new Version(4, 3),
                Profile = ContextProfile.Core, Title = "suitspec" };
            using var win = new NativeWindow(nws);
            win.MakeCurrent();
            int fbo = GL.GenFramebuffer(); GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            int col = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, col);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, W, H);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, col);
            int dep = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, dep);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, W, H);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, dep);
            var progs = new Dictionary<string, int>();
            int Prog(string vs, string fs)
            {
                string key = vs + "\0" + fs;
                return progs.TryGetValue(key, out int p) ? p : progs[key] = HdLook.Link(vs, fs);
            }
            int oldProg = Prog(GxShader.Vert, GunPrograms.SuitInterpFrag);
            int interp = Prog(GunPrograms.Vert, GunPrograms.SuitInterpFrag);
            int depthProg = Prog(GunPrograms.Vert, GunPrograms.DepthFrag);
            Console.WriteLine($"  interpreter fades: {GunPrograms.SuitFades}");

            Model dsModel = Read.GetModelInstance(Metadata.HunterModels[Hunter.Samus][0]).Model;
            var recipe = MphRecomp.Frontend.MenuSamus.Load();
            int suits = 0, same = 0;
            foreach (string dir in Directory.GetDirectories(converted).OrderBy(d => d))
            {
                string id = Path.GetFileName(dir);
                if (which != "all" && !id.Equals(which, StringComparison.OrdinalIgnoreCase)
                    && !id.Equals(which + "Ball", StringComparison.OrdinalIgnoreCase)) continue;
                string? dae = Directory.GetFiles(dir, "*.dae").FirstOrDefault();
                if (dae == null || !File.Exists(GxJson.PathFor(dae))) continue;
                List<GxMaterial> mats = GxJson.Read(GxJson.PathFor(dae));
                if (id.EndsWith("Ball", StringComparison.Ordinal))
                {
                    if (which == "all" && !Directory.Exists(Path.Combine(converted, id[..^4]))) continue;
                    int own = 0;
                    foreach (GxMaterial m in mats)
                    {
                        string? src = GunPrograms.SuitFrag(GxShader.Pack(m));
                        if (src != null) { Prog(GunPrograms.Vert, src); own++; }
                    }
                    Console.WriteLine($"  {id}: {own} of {mats.Count} materials on their own programs (compiled)");
                    continue;
                }
                if (!TrophyRigs.TryHunterFor(id, out Hunter h) || h != Hunter.Samus) continue;
                var sk = new DsSkeleton(dsModel);
                TrophyRig? rig = File.Exists(TrophyRigIO.PosedPathFor(dae)) ? TrophyRigIO.LoadPosedRig(dae, sk.Names) : null;
                foreach (string key in new[] { "pieces", "retro", "rigid" })
                {
                    if (rig != null) break;
                    var v = Array.Find(TrophyRigIO.Variants, x => x.Key == key);
                    if (v == null || !File.Exists(TrophyRigIO.VariantPathFor(dae, v))) continue;
                    rig = TrophyRigIO.LoadOrFitVariant(dae, dsModel, Array.Empty<Vector3>(), v, fit: false);
                }
                rig ??= TrophyRigIO.Load(Path.ChangeExtension(dae, ".mphrig"), TrophyRigIO.CacheKey(dae));
                if (rig == null) { Console.WriteLine($"  {id}: no rig cache, skipped"); continue; }
                suits++;

                // the posed body, one vertex buffer (11 floats: pos3 normal3 uv2 colour3 with blue/alpha packed)
                var model = DaeModel.Load(dae);
                var byName = new Dictionary<string, GxMaterial>();
                foreach (GxMaterial m in mats) byName.TryAdd(m.Name, m);
                var pose = new SkeletonPose(sk.Count);
                var world = new Matrix4[sk.Count];
                var bind = rig.BindPos ?? rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
                var posed = new Vector3[bind.Length];
                DsSkeleton.Sample(dsModel, recipe.Clip, recipe.Frame, pose);
                sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                HdRigTool.Deform(rig, bind, world, posed);
                var skin = new Matrix4[sk.Count];
                for (int i = 0; i < sk.Count; i++) skin[i] = rig.InvBind[i] * world[i];
                var verts = new List<float>();
                var batches = new List<Batch>();
                foreach (DaeMesh part in model.Meshes)
                {
                    if (!byName.TryGetValue(part.Material, out GxMaterial? gm)) continue;
                    int start = verts.Count / 11;
                    for (int t = 0; t + 2 < part.Positions.Count; t += 3)
                    {
                        int va = rig.IndexOf(part.Positions[t]), vb = rig.IndexOf(part.Positions[t + 1]), vc = rig.IndexOf(part.Positions[t + 2]);
                        if (va < 0 || vb < 0 || vc < 0 || rig.HidesTriangle(va, vb, vc)) continue;
                        foreach (int i in new[] { t, t + 1, t + 2 })
                        {
                            int vi = rig.IndexOf(part.Positions[i]);
                            Vector3 n = Vector3.TransformNormal(new Vector3(part.Normals[i].X, part.Normals[i].Y, part.Normals[i].Z), rig.TrophyToRig);
                            if (rig.BindRot != null) n = Vector3.Transform(n, rig.BindRot[vi]);
                            Vector3 acc = Vector3.Zero;
                            for (int k = 0; k < 4; k++)
                            {
                                float wt = rig.Weights[vi * 4 + k];
                                if (wt > 0) acc += Vector3.TransformVector(n, skin[rig.Bones[vi * 4 + k]]) * wt;
                            }
                            n = acc.LengthSquared > 1e-12f ? acc.Normalized() : Vector3.UnitY;
                            Vector3 q = posed[vi];
                            var uv = part.Uvs[i];
                            var cl = part.Colors.Count > i ? part.Colors[i] : part.FlatColor;
                            verts.AddRange(new[] { q.X, q.Y, q.Z, n.X, n.Y, n.Z, uv.X, uv.Y, cl.X, cl.Y, GxShader.PackBlueAlpha(cl.Z, cl.W) });
                        }
                    }
                    var P = GxShader.Pack(gm);
                    string? src = GunPrograms.SuitFrag(P);
                    batches.Add(new Batch { Start = start, Count = verts.Count / 11 - start, Gx = gm, P = P,
                        Pass = gm.Additive ? 2 : gm.BlendEnable ? 1 : 0, Prog = src != null ? Prog(GunPrograms.Vert, src) : interp });
                }
                int vao = GL.GenVertexArray(); GL.BindVertexArray(vao);
                int vbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * 4, verts.ToArray(), BufferUsageHint.StaticDraw);
                for (int a = 0; a < 4; a++) GL.EnableVertexAttribArray(a);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 44, 0);
                GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 44, 12);
                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 44, 24);
                GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 44, 32);
                var texCache = new Dictionary<string, int>();
                int Tex(string? name)
                {
                    name ??= "";
                    if (texCache.TryGetValue(name, out int t)) return t;
                    string f = Path.Combine(dir, name + ".png");
                    if (name == "" || !File.Exists(f)) return texCache[name] = HdLook.MakeTex(new byte[] { 255, 255, 255, 255 }, 1, 1);
                    using var img = Image.Load<Rgba32>(f);
                    var px = new byte[img.Width * img.Height * 4]; img.CopyPixelDataTo(px);
                    return texCache[name] = HdLook.MakeTex(px, img.Width, img.Height);
                }

                Vector3 lo = new(posed.Min(p => p.X), posed.Min(p => p.Y), posed.Min(p => p.Z));
                Vector3 hi = new(posed.Max(p => p.X), posed.Max(p => p.Y), posed.Max(p => p.Z));
                Vector3 c = (lo + hi) / 2;
                float ht = hi.Y - lo.Y;
                // Samus faces -Z, feet on y = 0 (CampaignSuit.DrawStill's space)
                var cams = new (string Name, Vector3 Eye, Vector3 Target)[]
                {
                    ("front", new Vector3(c.X + 0.4f, c.Y + 0.1f * ht, c.Z - 2.2f), c),
                    ("hip graze", new Vector3(c.X + 0.36f, lo.Y + 0.3f * ht, c.Z - 0.05f), new Vector3(c.X - 0.3f, c.Y + 0.15f * ht, c.Z + 0.2f)),
                    ("inside", new Vector3(c.X, c.Y + 0.15f * ht, c.Z), new Vector3(c.X + 0.2f, c.Y + 0.3f * ht, c.Z - 1f)),
                };
                int bad = 0, maxAll = 0;
                var notes = new List<string>();
                var pics = new List<(Image<Rgba32> Old, Image<Rgba32> New)>();
                foreach (var cam in cams)
                {
                    foreach (float alpha in new[] { 1f, 0.5f })
                    {
                        Matrix4 view = Matrix4.LookAt(cam.Eye, cam.Target, Vector3.UnitY);
                        Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(70 * MathF.PI / 180, (float)W / H, 0.05f, 100f);
                        var a = Render(false);
                        var b = Render(true);
                        int n = 0, max = 0, drawn = 0;
                        for (int y = 0; y < H; y++)
                            for (int x = 0; x < W; x++)
                            {
                                Rgba32 p = a[x, y], q = b[x, y];
                                if (p.R + p.G + p.B != 40 + 60 + 80) drawn++;
                                int dd = Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)));
                                max = Math.Max(max, dd);
                                if (dd > 2) n++;
                            }
                        notes.Add($"{cam.Name} a{alpha:0.0}: {n}/{drawn} (max {max})");
                        bad += n;
                        maxAll = Math.Max(maxAll, max);
                        pics.Add((a, b));

                        // the device's DrawBody: alpha 1 = pass 1 (Opaque), pass 4 (depth rebuild, colour masked), then the
                        // Translucent and Additive passes; alpha < 1 = the opaque parts moved to Translucent (DrawFaded)
                        Image<Rgba32> Render(bool spec)
                        {
                            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                            GL.Viewport(0, 0, W, H);
                            GL.ClearColor(40 / 255f, 60 / 255f, 80 / 255f, 1);
                            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                            GL.Enable(EnableCap.DepthTest);
                            GL.Disable(EnableCap.CullFace);
                            GL.BindVertexArray(vao);
                            Matrix4 mvp = view * proj, ident = Matrix4.Identity;
                            int cur = 0;
                            int U(string nm) => GL.GetUniformLocation(cur, nm);
                            void Use(int prog)
                            {
                                if (prog == cur) return;
                                cur = prog;
                                GL.UseProgram(prog);
                                GL.UniformMatrix4(U("uMvp"), false, ref mvp);
                                var vr = new Matrix3(view); GL.UniformMatrix3(U("uViewRot"), false, ref vr);
                                GL.Uniform1(U("uSkin"), 0);
                                GL.UniformMatrix4(U("uModel"), false, ref ident);
                                GL.Uniform3(U("uSceneAmb"), 102f / 255, 100f / 255, 100f / 255);
                                GL.Uniform1(U("uNumLights"), 1);
                                Vector3 ld = new Vector3(-50, 50, 90).Normalized();
                                GL.Uniform3(U("uLightDir"), 4, new[] { ld.X, ld.Y, ld.Z, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                                GL.Uniform3(U("uLightCol"), 4, new[] { 0.4f, 0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                                GL.Uniform3(U("uLightSpecCol"), 4, new[] { 1f, 1f, 1f, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                                GL.Uniform3(U("uLightSpecK"), 4, new[] { 64f, 0f, -63f, 1, 0, 0, 1, 0, 0, 1, 0, 0 });
                                GL.Uniform1(U("uExposure"), 1f); GL.Uniform1(U("uRawEnvNormal"), 0);
                                GL.Uniform4(U("uTint"), 0f, 0, 0, 0);
                                for (int t = 0; t < 8; t++) GL.Uniform1(U("uTex" + t), t);
                            }
                            void Material(Batch bt, float fade)
                            {
                                Use(spec ? bt.Prog : oldProg);
                                HdLook.Upload(cur, bt.P);
                                GL.Uniform1(U("uFade"), fade);
                                for (int u = 0; u < 8; u++)
                                {
                                    GL.ActiveTexture(TextureUnit.Texture0 + u);
                                    GxLayer? L = bt.P.Units[u];
                                    GL.BindTexture(TextureTarget.Texture2D, Tex(L?.Name));
                                    if (L != null) HdLook.Sampler(L);
                                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                                }
                                GL.ActiveTexture(TextureUnit.Texture0);
                            }
                            DepthFunction Func(int f, bool lequal) => (DepthFunction)((int)DepthFunction.Never + (lequal && f == 1 ? 3 : f));
                            void Emit(Batch bt, float fade, bool lequal)
                            {
                                Material(bt, fade);
                                if (bt.P.Blend)
                                {
                                    GL.Enable(EnableCap.Blend);
                                    GL.BlendFunc(HdLook.SrcFactor(bt.P.BlendSrc), HdLook.DstFactor(bt.P.BlendDst));
                                    GL.DepthMask(false);
                                }
                                else
                                {
                                    GL.Disable(EnableCap.Blend);
                                    GL.DepthMask(true);
                                }
                                GL.DepthFunc(Func(bt.P.DepthFunc, lequal));
                                GL.DrawArrays(PrimitiveType.Triangles, bt.Start, bt.Count);
                            }
                            void Depth()
                            {
                                GL.ColorMask(false, false, false, false);
                                Use(depthProg);
                                GL.Disable(EnableCap.Blend);
                                GL.DepthMask(true);
                                foreach (Batch bt in batches.Where(x => x.DepthFirst))
                                {
                                    GL.DepthFunc(Func(bt.P.DepthFunc, false));
                                    GL.DrawArrays(PrimitiveType.Triangles, bt.Start, bt.Count);
                                }
                                GL.ColorMask(true, true, true, true);
                            }
                            void PassOf(int pass, float fade, int depthDone) // 0 none, 1 lequal, 2 skip
                            {
                                for (int k = 0; k < (pass == 2 ? 2 : 1); k++)
                                    foreach (Batch bt in batches)
                                    {
                                        if (bt.Pass != pass || depthDone == 2 && bt.DepthFirst) continue;
                                        Emit(bt, fade, depthDone == 1 && bt.DepthFirst);
                                    }
                            }
                            if (alpha >= 0.999f)
                            {
                                // pass 1
                                if (spec) { Depth(); PassOf(0, 1f, 1); }
                                else PassOf(0, 1f, 0);
                                // pass 4: depth cleared, rebuilt with colour masked
                                GL.Clear(ClearBufferMask.DepthBufferBit);
                                GL.ColorMask(false, false, false, false);
                                if (spec) { Depth(); GL.ColorMask(false, false, false, false); PassOf(0, 1f, 2); }
                                else PassOf(0, 1f, 0);
                                GL.ColorMask(true, true, true, true);
                                PassOf(1, 1f, 0);
                                PassOf(2, 1f, 0);
                            }
                            else
                            {
                                // DrawFaded, then the blended and additive parts at the fade
                                if (spec) Depth();
                                GL.Enable(EnableCap.Blend);
                                GL.BlendFunc(BlendingFactor.Zero, BlendingFactor.One);
                                GL.DepthMask(true);
                                foreach (Batch bt in batches)
                                {
                                    if (bt.Pass != 0 || spec && bt.DepthFirst) continue;
                                    Material(bt, 1f);
                                    GL.DepthFunc(Func(bt.P.DepthFunc, false));
                                    GL.DrawArrays(PrimitiveType.Triangles, bt.Start, bt.Count);
                                }
                                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                                GL.DepthMask(false);
                                GL.DepthFunc(DepthFunction.Lequal);
                                foreach (Batch bt in batches)
                                {
                                    if (bt.Pass != 0) continue;
                                    Material(bt, alpha);
                                    GL.DrawArrays(PrimitiveType.Triangles, bt.Start, bt.Count);
                                }
                                PassOf(1, alpha, 0);
                                PassOf(2, alpha, 0);
                            }
                            GL.DepthMask(true);
                            GL.Disable(EnableCap.Blend);
                            ErrorCode err = GL.GetError();
                            if (err != ErrorCode.NoError) Console.WriteLine("  GL error " + err);
                            var px = new byte[W * H * 4];
                            GL.ReadPixels(0, 0, W, H, PixelFormat.Rgba, PixelType.UnsignedByte, px);
                            var img = new Image<Rgba32>(W, H);
                            for (int y = 0; y < H; y++)
                                for (int x = 0; x < W; x++)
                                {
                                    int o = ((H - 1 - y) * W + x) * 4;
                                    img[x, y] = new Rgba32(px[o], px[o + 1], px[o + 2], 255);
                                }
                            return img;
                        }
                    }
                }
                int ownN = batches.Count(x => x.Prog != interp);
                Console.WriteLine($"  {id}: {ownN} of {batches.Count} materials on their own programs, {batches.Count(x => x.DepthFirst)} depth first, "
                    + $"{verts.Count / 33} tris; differ > 2: {string.Join(", ", notes)}");
                if (bad == 0) same++;
                else
                {
                    using var img = new Image<Rgba32>(W * 3, H * pics.Count);
                    for (int k = 0; k < pics.Count; k++)
                        for (int y = 0; y < H; y++)
                            for (int x = 0; x < W; x++)
                            {
                                Rgba32 p = pics[k].Old[x, y], q = pics[k].New[x, y];
                                int dd = Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)));
                                img[x, k * H + y] = p;
                                img[W + x, k * H + y] = q;
                                img[2 * W + x, k * H + y] = dd > 2 ? new Rgba32(255, 0, 255) : new Rgba32((byte)(p.R / 3), (byte)(p.G / 3), (byte)(p.B / 3));
                            }
                    img.SaveAsPng(Path.Combine(outDir, $"suitspec_{id}.png"));
                }
                foreach (var (o, n) in pics) { o.Dispose(); n.Dispose(); }
                foreach (int t in texCache.Values) GL.DeleteTexture(t);
                GL.DeleteBuffer(vbo);
                GL.DeleteVertexArray(vao);
            }
            Console.WriteLine($"  suitspec: {same} of {suits} suit bodies draw the same");
        }
    }
}
