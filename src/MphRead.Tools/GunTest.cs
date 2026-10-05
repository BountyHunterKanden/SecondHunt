using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Assets;
using MphRecomp.Render;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // -guntest [room=UNIT2_LAND] [gun dir=brawl_extract/Converted/guns/mp1] [scale=auto|<number>]
    // The first-person Prime arm cannon driven by MPH's own gun state, headless: plays shot / charge / missile / weapon
    // switch through the campaign host, logs which Prime animation each MPH state maps to, measures MPH's SamusGun
    // against the Prime gun for placement, and writes a first-person GIF (left: MPH's gun, right: the Prime gun, both
    // flat shaded -- the device renders the real materials) to extract_out/campaign/gun_test.gif.
    internal static class GunTest
    {
        // <repo>/brawl_extract/Converted/guns/mp1 (this build's output is <repo>/src/MphRead.Tools/bin/<config>/net9.0)
        private static readonly string DefaultGunDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "brawl_extract", "Converted", "guns", "mp1"));
        // panel size; "big" as the 5th argument doubles it (for close looks at the stills)
        private static int W = 480, H = 270;

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            if (args.Length >= 2 && args[1] == "colours")
            {
                Colours();
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            string dir = args.Length >= 3 ? args[2] : DefaultGunDir;
            string scaleArg = args.Length >= 4 ? args[3] : "cfg";
            if (args.Length >= 5 && args[4] == "big")
            {
                W = 960;
                H = 540;
            }
            if (args.Length >= 6 && args[5] == "speccheck")
            {
                SpecCheck(room, dir);
                return;
            }
            HdGun gun = HdGun.Load(Path.Combine(dir, "gun.bin"));
            var vm = new GunViewmodel(gun);
            Console.WriteLine($"  gun: {gun.BoneCount} bones, {gun.Materials.Length} materials, {gun.Anims.Count} animations; "
                + $"muzzle bone {vm.MuzzleBone} charge bone {vm.ChargeBone}; hatch bones {vm.HatchBoneCount}");
            for (int m = 0; m < gun.Materials.Length; m++)
            {
                Console.WriteLine($"    mat {m}: {gun.Materials[m].VertexCount / 3} tris, palette {gun.Materials[m].Palette.Length}");
            }
            Vector3 restMuzzle = vm.RestMuzzle();
            (Vector3 pMin, Vector3 pMax) = Bounds(SkinnedTriangles(vm).SelectMany(t => t.V));
            Console.WriteLine($"  Prime gun at rest: min {pMin} max {pMax}; muzzle bone at {restMuzzle}");

            using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080,
                setupSave: s =>
                {
                    s.Weapons = 0xFF;
                    s.Ammo[0] = s.AmmoMax[0] = 4000;
                    s.Ammo[1] = s.AmmoMax[1] = 950;
                });
            Scene scene = host.Scene;
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            // the landing sequence holds input
            for (int i = 0; i < 960 && !host.Ended; i++)
            {
                host.Step(input);
            }
            host.Step(input);
            PlayerEntity player = host.Player;
            List<Tri> mph = MphGunTriangles(scene, player);
            (Vector3 mMin, Vector3 mMax) = Bounds(mph.SelectMany(t => t.V));
            Console.WriteLine($"  MPH SamusGun (gun space): {mph.Count} tris, min {mMin} max {mMax}; muzzle z {1548 / 4096f:0.###}");
            float scale = scaleArg == "auto" ? (mMax.Z - mMin.Z) / (pMax.Z - pMin.Z)
                : scaleArg == "cfg" ? gun.Scale
                : Single.Parse(scaleArg, System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"  scale {scale:0.####} ({scaleArg}; length match {(mMax.Z - mMin.Z) / (pMax.Z - pMin.Z):0.####}); "
                + $"MPH size {mMax - mMin}, Prime scaled {(pMax - pMin) * scale}");

            // "tintcheck" as the 6th argument: the resting gun drawn twice, Power Beam amber vs Shock Coil blue -- the pixels
            // that differ are exactly what takes MPH's light colour (magenta over the dimmed gun in gun_tintcheck.png)
            if (args.Length >= 6 && args[5] == "tintcheck")
            {
                for (int i = 0; i < 30; i++)
                {
                    host.Step(input);
                    vm.Update(player, 1 / 60f);
                }
                using var tgx = new PcGx(gun, dir, W, H);
                Matrix4 rest = vm.ModelMatrix(player, scale, restMuzzle);
                var amber = new Image<Rgba32>(W, H);
                var blue = new Image<Rgba32>(W, H);
                tgx.Render(vm, rest, scene.ViewMatrix, scene.PerspectiveMatrix, new Vector3(1, 0.74f, 0), 0, amber, 0);
                tgx.Render(vm, rest, scene.ViewMatrix, scene.PerspectiveMatrix, new Vector3(0, 0.52f, 1), 0, blue, 0);
                var diff = new Image<Rgba32>(W * 3, H);
                int changed = 0, drawn = 0;
                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        Rgba32 p = amber[x, y], q = blue[x, y];
                        bool gunPixel = p.R + p.G + p.B != 22 + 24 + 28;
                        bool differs = Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 12;
                        drawn += gunPixel ? 1 : 0;
                        changed += differs ? 1 : 0;
                        diff[x, y] = p;
                        diff[W + x, y] = q;
                        diff[2 * W + x, y] = differs ? new Rgba32(255, 0, 255) : new Rgba32((byte)(p.R / 3), (byte)(p.G / 3), (byte)(p.B / 3));
                    }
                }
                string tfile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign", "gun_tintcheck.png"));
                diff.SaveAsPng(tfile);
                Console.WriteLine($"  tint check: {changed} of {drawn} gun pixels take the light colour ({100.0 * changed / Math.Max(drawn, 1):0.0}%) -> {tfile}");
                return;
            }
            var script = new List<(int Frames, CampaignButtons Buttons, BeamType Weapon, string Label)>
            {
                (30, 0, BeamType.None, "idle"),
                (2, CampaignButtons.Shoot, BeamType.None, "shot"), (40, 0, BeamType.None, ""),
                (130, CampaignButtons.Shoot, BeamType.None, "charge"), (60, 0, BeamType.None, "charged shot"),
                (2, 0, BeamType.Missile, "missile"), (40, 0, BeamType.None, ""),
                (2, CampaignButtons.Shoot, BeamType.None, "missile shot"), (50, 0, BeamType.None, ""),
                (130, CampaignButtons.Shoot, BeamType.None, "missile charge"), (60, 0, BeamType.None, "charged missile"),
                (2, 0, BeamType.PowerBeam, "power"), (40, 0, BeamType.None, ""),
                (2, 0, BeamType.VoltDriver, "volt driver"), (60, 0, BeamType.None, ""),
                (2, 0, BeamType.PowerBeam, "power"), (60, 0, BeamType.None, ""),
                (2, 0, BeamType.Battlehammer, "battlehammer"), (70, 0, BeamType.None, ""),
                (2, 0, BeamType.Judicator, "judicator"), (70, 0, BeamType.None, ""),
                (2, 0, BeamType.ShockCoil, "shock coil"), (70, 0, BeamType.None, ""),
                (2, 0, BeamType.Imperialist, "imperialist"), (70, 0, BeamType.None, "")
            };
            using var gx = new PcGx(gun, dir, W, H);
            var gif = new Image<Rgba32>(W * 2, H);
            bool firstFrame = true;
            string last = "";
            int frame = 0;
            float maxMuzzleDrift = 0;
            foreach ((int frames, CampaignButtons buttons, BeamType weapon, string label) in script)
            {
                if (label != "")
                {
                    Console.WriteLine($"  -- {label}");
                }
                for (int i = 0; i < frames; i++, frame++)
                {
                    input.Buttons = buttons;
                    input.SelectWeapon = weapon;
                    host.Step(input);
                    vm.Update(player, 1 / 60f);
                    string now = $"{player.GunAnimation} -> {vm.Playing}";
                    if (now != last)
                    {
                        Console.WriteLine($"    f{frame,4} {now}  (MPH frame {player.GunAnimFrame}/{player.GunAnimFrameCount}, "
                            + $"charge {player.EquipInfo.ChargeLevel}, weapon {player.EquipInfo.Weapon?.Beam})");
                        last = now;
                    }
                    Matrix4 model = vm.ModelMatrix(player, scale, restMuzzle);
                    if (player.GunAnimation == GunAnimation.Idle && vm.Playing == "powerBasePosition")
                    {
                        Vector3 muzzle = (new Vector4(vm.BonePosition(vm.MuzzleBone), 1) * model).Xyz;
                        maxMuzzleDrift = MathF.Max(maxMuzzleDrift, (muzzle - player.GunMuzzlePosition).Length);
                    }
                    if (frame % 2 == 0)
                    {
                        Matrix4 viewProj = scene.ViewMatrix * scene.PerspectiveMatrix;
                        var img = new Image<Rgba32>(W * 2, H, new Rgba32(22, 24, 28));
                        Raster(img, 0, MphGunTriangles(scene, player, world: true), viewProj);
                        GunViewmodel.TryMphLightColour(scene, out Vector3 tint);
                        gx.Render(vm, model, scene.ViewMatrix, scene.PerspectiveMatrix, tint, frame / 60.0, img, W);
                        // exact frames (the GIF's palette can hide or invent detail): every 100th, next to the GIF --
                        // or every one drawn with "frames" as the 6th argument (for composites built from PNGs)
                        if (frame % 100 == 0 || args.Length >= 6 && args[5] == "frames")
                        {
                            string stills = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign", "gun_stills"));
                            Directory.CreateDirectory(stills);
                            img.SaveAsPng(Path.Combine(stills, $"{Path.GetFileName(dir.TrimEnd('\\', '/'))}_{frame:000}.png"));
                        }
                        Crosshair(img, 0); Crosshair(img, W);
                        if (firstFrame)
                        {
                            gif.Frames.InsertFrame(0, img.Frames.RootFrame);
                            gif.Frames.RemoveFrame(1);
                            firstFrame = false;
                        }
                        else
                        {
                            gif.Frames.AddFrame(img.Frames.RootFrame);
                        }
                        img.Dispose();
                    }
                }
            }
            Console.WriteLine($"  idle muzzle bone vs MPH muzzle (should be ~0 -- aligned by construction): {maxMuzzleDrift:0.####}");
            foreach (ImageFrame<Rgba32> f in gif.Frames)
            {
                f.Metadata.GetGifMetadata().FrameDelay = 3; // 1/100 s units: ~30 fps
            }
            gif.Metadata.GetGifMetadata().RepeatCount = 0;
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign"));
            Directory.CreateDirectory(outDir);
            string file = Path.Combine(outDir, "gun_test.gif");
            gif.SaveAsGif(file);
            Console.WriteLine($"  wrote {file} ({gif.Frames.Count} frames)");
        }

        // -guntest colours: MPH's SamusGun per weapon -- every material's colours as drawn (the per-weapon material
        // animation, slot 1), to find the light materials and their colour per weapon
        private static void Colours()
        {
            using CampaignHost host = CampaignHost.Start("UNIT2_LAND", collectDrawItems: true, viewWidth: 1920, viewHeight: 1080,
                setupSave: s =>
                {
                    s.Weapons = 0xFF;
                    s.Ammo[0] = s.AmmoMax[0] = 4000;
                    s.Ammo[1] = s.AmmoMax[1] = 950;
                });
            Scene scene = host.Scene;
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            for (int i = 0; i < 960; i++)
            {
                host.Step(input);
            }
            foreach (BeamType w in new[] { BeamType.PowerBeam, BeamType.VoltDriver, BeamType.Missile, BeamType.Battlehammer,
                BeamType.Imperialist, BeamType.Judicator, BeamType.Magmaul, BeamType.ShockCoil })
            {
                input.SelectWeapon = w;
                host.Step(input);
                input.SelectWeapon = BeamType.None;
                for (int i = 0; i < 90; i++)
                {
                    host.Step(input);
                }
                Console.WriteLine($"  {w} (now {host.Player.CurrentWeapon}, state {host.Player.GunAnimation}):");
                var seen = new HashSet<string>();
                foreach (IReadOnlyList<RenderItem> list in new[] { scene.OpaqueItems, scene.DecalItems, scene.TranslucentItems })
                {
                    foreach (RenderItem item in list)
                    {
                        if (item.Type != RenderItemType.Mesh || item.ListId < 1 || item.ListId > scene.HostMeshes.Count)
                        {
                            continue;
                        }
                        (Model model, Mesh mesh, bool _) = scene.HostMeshes[item.ListId - 1];
                        if (model.Name != "SamusGun")
                        {
                            continue;
                        }
                        Material mat = model.Materials[mesh.MaterialId];
                        string line = $"    {mat.Name,-16} dif {Fmt(item.Diffuse)} amb {Fmt(item.Ambient)} emi {Fmt(item.Emission)} "
                            + $"alpha {item.Alpha:0.00} tex {item.HasTexture} lit {item.Lighting} {item.RenderMode}"
                            + (item.OverrideColor.HasValue ? $" override {item.OverrideColor}" : "")
                            + (item.PaletteOverride.HasValue ? $" palette {item.PaletteOverride}" : "");
                        if (seen.Add(line))
                        {
                            Console.WriteLine(line);
                        }
                    }
                }
            }
            static string Fmt(Vector3 v) => $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})";
        }

        // "speccheck" as the 6th argument (dir = one gun, or a folder of gun dirs): each gun at rest drawn the way the device
        // drew it until 2026-10-03 (the GX interpreter, colour and depth in one go) and the way it draws it now (GunPrograms:
        // the gun's depth first, then each material's own TEV program), with two weapon colours; prints how many gun
        // pixels differ and writes extract_out/campaign/speccheck_<gun>.png (old | new | differences in magenta) when any do
        private static void SpecCheck(string room, string dir)
        {
            string[] dirs = File.Exists(Path.Combine(dir, "gun.bin")) ? new[] { dir }
                : Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "gun.bin"))).OrderBy(d => d).ToArray();
            using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080,
                setupSave: s =>
                {
                    s.Weapons = 0xFF;
                    s.Ammo[0] = s.AmmoMax[0] = 4000;
                    s.Ammo[1] = s.AmmoMax[1] = 950;
                });
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            for (int i = 0; i < 990 && !host.Ended; i++)
            {
                host.Step(input);
            }
            PlayerEntity player = host.Player;
            Scene scene = host.Scene;
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign"));
            int failed = 0;
            foreach (string d in dirs)
            {
                string name = Path.GetFileName(d.TrimEnd('\\', '/'));
                HdGun gun = HdGun.Load(Path.Combine(d, "gun.bin"));
                var vm = new GunViewmodel(gun);
                for (int i = 0; i < 30; i++) vm.Update(player, 1 / 60f);
                Matrix4 rest = vm.ModelMatrix(player, gun.Scale, vm.RestMuzzle());
                using var gx = new PcGx(gun, d, W, H);
                var line = new List<string>();
                var pics = new List<(Image<Rgba32> Old, Image<Rgba32> New)>();
                int worstDiff = 0, differ = 0;
                foreach (Vector3 tint in new[] { new Vector3(1, 0.74f, 0), new Vector3(0, 0.52f, 1) })
                {
                    var a = new Image<Rgba32>(W, H);
                    var b = new Image<Rgba32>(W, H);
                    gx.Render(vm, rest, scene.ViewMatrix, scene.PerspectiveMatrix, tint, 0, a, 0);
                    gx.Render(vm, rest, scene.ViewMatrix, scene.PerspectiveMatrix, tint, 0, b, 0, spec: true);
                    int drawn = 0, n = 0, max = 0;
                    for (int y = 0; y < H; y++)
                    {
                        for (int x = 0; x < W; x++)
                        {
                            Rgba32 p = a[x, y], q = b[x, y];
                            if (p.R + p.G + p.B != 22 + 24 + 28 || q.R + q.G + q.B != 22 + 24 + 28) drawn++;
                            int dd = Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)));
                            max = Math.Max(max, dd);
                            if (dd > 2) n++;
                        }
                    }
                    line.Add($"{(tint.X > 0.5f ? "amber" : "blue")} {n} of {drawn} gun pixels differ by > 2 (max {max})");
                    pics.Add((a, b));
                    worstDiff = Math.Max(worstDiff, max);
                    differ += n;
                }
                Console.WriteLine($"  {name}: {gx.OwnPrograms} of {gx.DrawnMaterials} materials on their own programs; {string.Join("; ", line)}");
                if (differ > 0)
                {
                    failed++;
                    var img = new Image<Rgba32>(W * 3, H * pics.Count);
                    for (int k = 0; k < pics.Count; k++)
                    {
                        for (int y = 0; y < H; y++)
                        {
                            for (int x = 0; x < W; x++)
                            {
                                Rgba32 p = pics[k].Old[x, y], q = pics[k].New[x, y];
                                int dd = Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)));
                                img[x, k * H + y] = p;
                                img[W + x, k * H + y] = q;
                                img[2 * W + x, k * H + y] = dd > 2 ? new Rgba32(255, 0, 255) : new Rgba32((byte)(p.R / 3), (byte)(p.G / 3), (byte)(p.B / 3));
                            }
                        }
                    }
                    img.SaveAsPng(Path.Combine(outDir, $"speccheck_{name}.png"));
                }
            }
            Console.WriteLine($"  speccheck: {dirs.Length - failed} of {dirs.Length} guns draw the same");
        }

        // the Prime gun through the GX program the Odin runs (MphRecomp.Render.GxShader, offscreen): its own materials,
        // skinned on the GPU, the light materials tinted with MPH's per-weapon colour; the trophy viewer's light (as the
        // device's HD suits use)
        private sealed class PcGx : IDisposable
        {
            private readonly NativeWindow _win;
            private readonly int _prog, _fbo, _vao, _w, _h;
            private readonly HdGun _gun;
            private readonly List<GxMaterial> _mats;
            private readonly int[] _start, _count;
            private readonly Dictionary<string, int> _tex = new();
            private readonly string _dir;
            private readonly float[] _pal = new float[32 * 16];
            // speccheck: the device's programs now (GunPrograms), made on the first spec render
            private int _depthProg;
            private int[]? _matProg;
            public int OwnPrograms, DrawnMaterials;

            public PcGx(HdGun gun, string dir, int w, int h)
            {
                _gun = gun; _dir = dir; _w = w; _h = h;
                _win = new NativeWindow(new NativeWindowSettings { ClientSize = new Vector2i(64, 64), StartVisible = false,
                    APIVersion = new Version(4, 3), Profile = ContextProfile.Core, Title = "guntest" });
                _win.MakeCurrent();
                _prog = HdLook.Link(GxShader.Vert, GxShader.Frag);
                _fbo = GL.GenFramebuffer(); GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
                int col = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, col);
                GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
                GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, col);
                int dep = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, dep);
                GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, w, h);
                GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, dep);
                _mats = GxJson.Read(Path.Combine(dir, "gun.gx.json"));
                // one buffer, every material's vertices back to back (19 floats: GxShader's skinned layout)
                _start = new int[gun.Materials.Length]; _count = new int[gun.Materials.Length];
                var all = new List<float>();
                for (int m = 0; m < gun.Materials.Length; m++)
                {
                    _start[m] = all.Count / HdGun.FloatsPerVertex; _count[m] = gun.Materials[m].VertexCount;
                    all.AddRange(gun.Materials[m].Vertices);
                }
                _vao = GL.GenVertexArray(); GL.BindVertexArray(_vao);
                int vbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, all.Count * 4, all.ToArray(), BufferUsageHint.StaticDraw);
                int stride = HdGun.FloatsPerVertex * 4;
                int[] size = { 3, 3, 2, 3, 4, 4 }, off = { 0, 3, 6, 8, 11, 15 };
                for (int a = 0; a < 6; a++)
                {
                    GL.EnableVertexAttribArray(a);
                    GL.VertexAttribPointer(a, size[a], VertexAttribPointerType.Float, false, stride, off[a] * 4);
                }
            }

            private int Tex(string? name)
            {
                name ??= "";
                if (_tex.TryGetValue(name, out int id)) return id;
                string f = Path.Combine(_dir, name + ".png");
                if (name == "" || !File.Exists(f)) return _tex[name] = HdLook.MakeTex(new byte[] { 255, 255, 255, 255 }, 1, 1);
                using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(f);
                var px = new byte[img.Width * img.Height * 4]; img.CopyPixelDataTo(px);
                return _tex[name] = HdLook.MakeTex(px, img.Width, img.Height);
            }

            private void EnsureSpec()
            {
                if (_matProg != null) return;
                _depthProg = HdLook.Link(GunPrograms.Vert, GunPrograms.DepthFrag);
                int interp = HdLook.Link(GunPrograms.Vert, GxShader.Frag);
                _matProg = new int[_gun.Materials.Length];
                for (int m = 0; m < _gun.Materials.Length && m < _mats.Count; m++)
                {
                    var P = GxShader.Pack(_mats[m]);
                    string? src = P.Blend ? null : GunPrograms.Frag(_gun, m, P);
                    _matProg[m] = src != null ? HdLook.Link(GunPrograms.Vert, src) : interp;
                    DrawnMaterials += P.Blend ? 0 : 1;
                    OwnPrograms += src != null ? 1 : 0;
                }
            }

            // spec: as the device draws it now (CampaignGun: GunPass.Prepass, then AfterPrepass); else the interpreter alone
            public void Render(GunViewmodel vm, Matrix4 model, Matrix4 view, Matrix4 proj, Vector3 tint, double seconds, Image<Rgba32> dst, int ox,
                bool spec = false)
            {
                if (spec) EnsureSpec();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
                GL.Viewport(0, 0, _w, _h);
                GL.ClearColor(22 / 255f, 24 / 255f, 28 / 255f, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                GL.Enable(EnableCap.DepthTest);
                Matrix4 mvp = view * proj;
                int cur = 0;
                int U(string n) => GL.GetUniformLocation(cur, n);
                void Use(int prog)
                {
                    if (prog == cur) return;
                    cur = prog;
                    GL.UseProgram(prog);
                    GL.UniformMatrix4(U("uMvp"), false, ref mvp);
                    var vr = new Matrix3(view); GL.UniformMatrix3(U("uViewRot"), false, ref vr);
                    GL.Uniform1(U("uSkin"), 1);
                    GL.UniformMatrix4(U("uModel"), false, ref model);
                    GL.Uniform3(U("uSceneAmb"), 102f / 255, 100f / 255, 100f / 255);
                    GL.Uniform1(U("uNumLights"), 1);
                    Vector3 ld = new Vector3(-50, 50, 90).Normalized();
                    GL.Uniform3(U("uLightDir"), 4, new[] { ld.X, ld.Y, ld.Z, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                    GL.Uniform3(U("uLightCol"), 4, new[] { 0.4f, 0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                    GL.Uniform3(U("uLightSpecCol"), 4, new[] { 1f, 1f, 1f, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                    GL.Uniform3(U("uLightSpecK"), 4, new[] { 64f, 0f, -63f, 1, 0, 0, 1, 0, 0, 1, 0, 0 });
                    GL.Uniform1(U("uExposure"), 1f); GL.Uniform1(U("uRawEnvNormal"), 0);
                    for (int t = 0; t < 8; t++) GL.Uniform1(U("uTex" + t), t);
                }
                GL.BindVertexArray(_vao);
                if (spec)
                {
                    // the depth prepass: opaque, not alpha-tested materials, no colour
                    GL.ColorMask(false, false, false, false);
                    Use(_depthProg);
                    for (int m = 0; m < _gun.Materials.Length && m < _mats.Count; m++)
                    {
                        GxMaterial gm = _mats[m];
                        if (gm.BlendEnable || gm.AlphaTest) continue;
                        vm.FillPalette(m, _pal);
                        GL.UniformMatrix4(U("uBones"), _gun.Materials[m].Palette.Length, false, _pal);
                        GL.DepthMask(gm.DepthWrite);
                        GL.DepthFunc((DepthFunction)((int)DepthFunction.Never + gm.DepthFunc));
                        GL.DrawArrays(PrimitiveType.Triangles, _start[m], _count[m]);
                    }
                    GL.ColorMask(true, true, true, true);
                }
                else Use(_prog);
                foreach (bool blendPass in new[] { false, true })
                {
                    for (int m = 0; m < _gun.Materials.Length && m < _mats.Count; m++)
                    {
                        GxMaterial gm = _mats[m];
                        if (gm.BlendEnable != blendPass) continue;
                        var P = GxShader.Pack(gm);
                        if (P.Scrolls) P.TexMtx = GxShader.ScrolledTexMtx(gm, P, seconds);
                        Use(spec ? _matProg![m] : _prog);
                        HdLook.Upload(cur, P);
                        GL.Uniform4(U("uTint"), tint.X, tint.Y, tint.Z, GunPrograms.TintMode(_gun, m, P));
                        GL.Uniform1(U("uTintUnit"), _gun.GlowUnit);
                        vm.FillPalette(m, _pal);
                        GL.UniformMatrix4(U("uBones"), _gun.Materials[m].Palette.Length, false, _pal);
                        for (int u = 0; u < 8; u++)
                        {
                            GL.ActiveTexture(TextureUnit.Texture0 + u);
                            GxLayer? L = P.Units[u];
                            GL.BindTexture(TextureTarget.Texture2D, Tex(L?.Name));
                            if (L != null) HdLook.Sampler(L);
                            // as the device draws the gun (CampaignGun): always trilinear -- a converted layer's default
                            // (linear, no mips) aliases fine detail into speckle once the gun is small on screen
                            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                        }
                        GL.ActiveTexture(TextureUnit.Texture0);
                        if (P.Blend)
                        {
                            GL.Enable(EnableCap.Blend);
                            GL.BlendFunc(HdLook.SrcFactor(P.BlendSrc), HdLook.DstFactor(P.BlendDst));
                        }
                        else GL.Disable(EnableCap.Blend);
                        GL.DepthMask(P.DepthWrite && !P.Blend);
                        int func = P.DepthFunc == 1 && spec && !P.Blend && P.AlphaTest[3] != 1 ? 3 : P.DepthFunc; // < -> <= over the prepass
                        GL.DepthFunc((DepthFunction)((int)DepthFunction.Never + func));
                        GL.DrawArrays(PrimitiveType.Triangles, _start[m], _count[m]);
                    }
                }
                GL.DepthMask(true);
                ErrorCode err = GL.GetError();
                if (err != ErrorCode.NoError) Console.WriteLine("  GL error " + err);
                var px = new byte[_w * _h * 4];
                GL.ReadPixels(0, 0, _w, _h, PixelFormat.Rgba, PixelType.UnsignedByte, px);
                for (int y = 0; y < _h; y++)
                    for (int x = 0; x < _w; x++)
                    {
                        int o = ((_h - 1 - y) * _w + x) * 4;
                        dst[ox + x, y] = new Rgba32(px[o], px[o + 1], px[o + 2], 255);
                    }
            }

            public void Dispose() => _win.Dispose();
        }

        private sealed class Tri
        {
            public Vector3[] V = new Vector3[3];
            public Rgba32 Colour;
        }

        private static (Vector3, Vector3) Bounds(IEnumerable<Vector3> points)
        {
            var min = new Vector3(Single.MaxValue);
            var max = new Vector3(Single.MinValue);
            foreach (Vector3 p in points)
            {
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
            return (min, max);
        }

        // the lights (mats 0, 2, 5 -- the ones MPH's weapon colours will tint) in orange, the body grey
        private static readonly HashSet<int> _lightMaterials = new() { 0, 2, 5 };

        private static List<Tri> SkinnedTriangles(GunViewmodel vm, Matrix4? model = null)
        {
            var tris = new List<Tri>();
            HdGun g = vm.Gun;
            var pal = new float[32 * 16];
            for (int m = 0; m < g.Materials.Length; m++)
            {
                HdGun.Material mat = g.Materials[m];
                vm.FillPalette(m, pal);
                var colour = _lightMaterials.Contains(m) ? new Rgba32(255, 150, 60) : m == 7 ? new Rgba32(120, 200, 255) : new Rgba32(170, 175, 185);
                for (int v = 0; v < mat.VertexCount; v += 3)
                {
                    var t = new Tri { Colour = colour };
                    for (int k = 0; k < 3; k++)
                    {
                        int o = (v + k) * HdGun.FloatsPerVertex;
                        var pos = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
                        Vector4 sum = Vector4.Zero;
                        for (int j = 0; j < 4; j++)
                        {
                            float w = mat.Vertices[o + 15 + j];
                            if (w <= 0)
                            {
                                continue;
                            }
                            int b = (int)mat.Vertices[o + 11 + j];
                            int po = b * 16;
                            var skin = new Matrix4(pal[po], pal[po + 1], pal[po + 2], pal[po + 3], pal[po + 4], pal[po + 5], pal[po + 6], pal[po + 7],
                                pal[po + 8], pal[po + 9], pal[po + 10], pal[po + 11], pal[po + 12], pal[po + 13], pal[po + 14], pal[po + 15]);
                            sum += pos * skin * w;
                        }
                        t.V[k] = model.HasValue ? (sum * model.Value).Xyz : sum.Xyz;
                    }
                    tris.Add(t);
                }
            }
            return tris;
        }

        // MPH's own first-person gun as drawn this frame: world space, or gun space (MPH's gun matrix undone)
        private static List<Tri> MphGunTriangles(Scene scene, PlayerEntity player, bool world = false)
        {
            var tris = new List<Tri>();
            Matrix4 toGun = player.GunDrawTransform.Inverted();
            foreach (IReadOnlyList<RenderItem> list in new[] { scene.OpaqueItems, scene.DecalItems, scene.TranslucentItems })
            {
                foreach (RenderItem item in list)
                {
                    if (item.Type != RenderItemType.Mesh || item.ListId < 1 || item.ListId > scene.HostMeshes.Count)
                    {
                        continue;
                    }
                    (Model model, Mesh mesh, bool isRoom) = scene.HostMeshes[item.ListId - 1];
                    if (model.Name != "SamusGun")
                    {
                        continue;
                    }
                    float[] v = MphRecomp.Render.DsDisplayList.Decode(model, mesh, isRoom);
                    int stride = MphRecomp.Render.DsDisplayList.Stride;
                    for (int i = 0; i + 3 * stride <= v.Length; i += 3 * stride)
                    {
                        var t = new Tri { Colour = new Rgba32(170, 175, 185) };
                        for (int k = 0; k < 3; k++)
                        {
                            int o = i + k * stride;
                            Matrix4 m = item.Transform;
                            if (item.MatrixStackCount > 0)
                            {
                                int s = (int)(v[o + 12] + 0.5f) * 16;
                                float[] ms = item.MatrixStack;
                                m = new Matrix4(ms[s], ms[s + 1], ms[s + 2], ms[s + 3], ms[s + 4], ms[s + 5], ms[s + 6], ms[s + 7],
                                    ms[s + 8], ms[s + 9], ms[s + 10], ms[s + 11], ms[s + 12], ms[s + 13], ms[s + 14], ms[s + 15]);
                            }
                            Vector4 p = new Vector4(v[o], v[o + 1], v[o + 2], 1) * m;
                            t.V[k] = world ? p.Xyz : (p * toGun).Xyz;
                        }
                        tris.Add(t);
                    }
                }
            }
            return tris;
        }

        // z-buffered flat-shaded triangles into a W x H panel at x offset ox
        private static void Raster(Image<Rgba32> img, int ox, List<Tri> tris, Matrix4 viewProj)
        {
            var depth = new float[W * H];
            Array.Fill(depth, Single.MaxValue);
            var s = new Vector3[3];
            foreach (Tri t in tris)
            {
                bool ok = true;
                for (int k = 0; k < 3; k++)
                {
                    Vector4 c = new Vector4(t.V[k], 1) * viewProj;
                    if (c.W < 0.001f)
                    {
                        ok = false;
                        break;
                    }
                    s[k] = new Vector3((c.X / c.W * 0.5f + 0.5f) * W, (0.5f - c.Y / c.W * 0.5f) * H, c.Z / c.W);
                }
                if (!ok)
                {
                    continue;
                }
                Vector3 n = Vector3.Cross(t.V[1] - t.V[0], t.V[2] - t.V[0]);
                float len = n.Length;
                if (len < 1e-9f)
                {
                    continue;
                }
                // shade by the angle to a light over the viewer's right shoulder, in screen terms
                Vector3 e1 = s[1] - s[0], e2 = s[2] - s[0];
                float area = e1.X * e2.Y - e1.Y * e2.X;
                if (MathF.Abs(area) < 1e-6f)
                {
                    continue;
                }
                var sn = Vector3.Normalize(Vector3.Cross(new Vector3(e1.X, e1.Y, e1.Z * 50), new Vector3(e2.X, e2.Y, e2.Z * 50)));
                float lit = 0.35f + 0.65f * MathF.Abs(Vector3.Dot(sn, Vector3.Normalize(new Vector3(-0.3f, -0.4f, 1))));
                var col = new Rgba32((byte)(t.Colour.R * lit), (byte)(t.Colour.G * lit), (byte)(t.Colour.B * lit));
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(s[0].X, MathF.Min(s[1].X, s[2].X))));
                int x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(s[0].X, MathF.Max(s[1].X, s[2].X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(s[0].Y, MathF.Min(s[1].Y, s[2].Y))));
                int y1 = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(s[0].Y, MathF.Max(s[1].Y, s[2].Y))));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float w0 = ((s[1].X - px) * (s[2].Y - py) - (s[1].Y - py) * (s[2].X - px)) / area;
                        float w1 = ((s[2].X - px) * (s[0].Y - py) - (s[2].Y - py) * (s[0].X - px)) / area;
                        float w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0)
                        {
                            continue;
                        }
                        float z = w0 * s[0].Z + w1 * s[1].Z + w2 * s[2].Z;
                        int di = y * W + x;
                        if (z < depth[di])
                        {
                            depth[di] = z;
                            img[ox + x, y] = col;
                        }
                    }
                }
            }
        }

        private static void Crosshair(Image<Rgba32> img, int ox)
        {
            for (int d = -4; d <= 4; d++)
            {
                img[ox + W / 2 + d, H / 2] = new Rgba32(90, 255, 120);
                img[ox + W / 2, H / 2 + d] = new Rgba32(90, 255, 120);
            }
        }
    }
}
