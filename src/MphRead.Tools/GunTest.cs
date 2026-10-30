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
            if (args.Length >= 2 && args[1] == "beamtint")
            {
                BeamTint(args.Length >= 3 ? Enum.Parse<Hunter>(args[2]) : Hunter.Sylux, args.Length >= 4 ? args[3] : null);
                return;
            }
            if (args.Length >= 2 && args[1] == "vanillabeams")
            {
                VanillaBeams(args.Length >= 3 ? Enum.Parse<Hunter>(args[2]) : Hunter.Sylux);
                return;
            }
            if (args.Length >= 2 && args[1] == "vanilla")
            {
                Vanilla(args.Length >= 3 ? Enum.Parse<Hunter>(args[2]) : Hunter.Sylux);
                return;
            }
            if (args.Length >= 3 && args[1] == "clips")
            {
                Clips(args[2], args.Length >= 4 ? Enum.Parse<Hunter>(args[3]) : Hunter.Sylux, args.Length >= 5 ? args[4] : "MP3 PROVING GROUND",
                    args.Length >= 6 ? args[5] : "");
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
                (90, CampaignButtons.Shoot, BeamType.None, "shock coil held"), (60, 0, BeamType.None, "released"),
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

        // -guntest beamtint [hunter]: a match as the hunter, each beam equipped in turn; the light colour the app tints a Prime
        // gun's glow with (GunViewmodel.TryMphLightColour on the hunter's own MPH gun) per beam; with a gun dir, that Prime gun
        // at rest in first person per beam, as extract_out/campaign/gun_clips/beamtint/<NN_beam>.png
        private static void BeamTint(Hunter hunter, string? dir)
        {
            W = 800;
            H = 450;
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, "MP3 PROVING GROUND", hunter, 1);
            settings.Players[1].BotLevel = 0;
            using CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
            var input = new CampaignInput();
            int alive = 0;
            for (int i = 0; i < 3000 && !host.Ended && alive < 90; i++)
            {
                host.Step(input);
                alive = host.Player.Health > 0 ? alive + 1 : 0;
            }
            PlayerEntity player = host.Player;
            string mphGun = Metadata.HunterModels[player.Hunter][3];
            HdGun? gun = dir != null ? HdGun.Load(Path.Combine(dir, "gun.bin")) : null;
            GunViewmodel? vm = gun != null ? new GunViewmodel(gun) : null;
            using PcGx? gx = gun != null ? new PcGx(gun, dir!, W, H) : null;
            string? outDir = gun != null ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign", "gun_clips", "beamtint")) : null;
            if (outDir != null) Directory.CreateDirectory(outDir);
            var ammo = (int[])typeof(PlayerEntity).GetField("_ammo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(player)!;
            foreach (BeamType beam in new[] { BeamType.PowerBeam, BeamType.VoltDriver, BeamType.Missile, BeamType.Battlehammer, BeamType.Imperialist,
                BeamType.Judicator, BeamType.Magmaul, BeamType.ShockCoil, BeamType.OmegaCannon })
            {
                player.AvailableWeapons[beam] = true;
                ammo[0] = ammo[1] = 100;
                bool ok = player.HostEquipWeapon(beam);
                for (int i = 0; i < 40; i++) host.Step(input);
                bool drawn = GunViewmodel.TryMphLightColour(host.Scene, out Vector3 tint, mphGun);
                Console.WriteLine($"  {beam,-13} equipped {ok,-5} gun drawn {drawn,-5} tint {tint.X * 255:0},{tint.Y * 255:0},{tint.Z * 255:0}");
                if (gx != null && vm != null)
                {
                    vm.Update(player, 1 / 60f);
                    vm.PoseClip("powerBasePosition", 0);
                    Matrix4 model = vm.ModelMatrix(player, vm.Gun.Scale, vm.RestMuzzle());
                    using var panel = new Image<Rgba32>(W, H, new Rgba32(22, 24, 28));
                    gx.Render(vm, model, host.Scene.ViewMatrix, host.Scene.PerspectiveMatrix, tint, 0, panel, 0);
                    panel.SaveAsPng(Path.Combine(outDir!, $"{(int)beam:00}_{beam}.png"));
                }
            }
        }

        // -guntest vanillabeams [hunter]: what the per-beam gun animations (GunAnimationIds columns 1-9, the Idle row) change on
        // the hunter's MPH gun model: which materials they recolour (diffuse at frame 0) and which they swap textures on
        private static void VanillaBeams(Hunter hunter)
        {
            string gunName = Metadata.HunterModels[hunter][3];
            ModelInstance inst = Read.GetModelInstance(gunName, noCache: true);
            Model model = inst.Model;
            Console.WriteLine($"  {gunName} materials:");
            foreach (Material m in model.Materials)
            {
                Console.WriteLine($"    {m.Name,-24} diffuse {m.Diffuse.Red},{m.Diffuse.Green},{m.Diffuse.Blue}");
            }
            for (int c = 1; c < 10; c++)
            {
                int id = Metadata.GunAnimationIds[(int)hunter, (int)GunAnimation.Idle, c];
                var beam = (BeamType)(c - 1);
                if (id < 0)
                {
                    Console.WriteLine($"  {beam,-13} (none)");
                    continue;
                }
                MaterialAnimationGroup mg = model.AnimationGroups.Material[id];
                TextureAnimationGroup tg = model.AnimationGroups.Texture[id];
                TexcoordAnimationGroup cg = model.AnimationGroups.Texcoord[id];
                inst.SetAnimation(id, slot: 0, SetFlags.Material | SetFlags.Texture | SetFlags.Texcoord);
                inst.AnimInfo.Frame[0] = 0;
                model.AnimateMaterials(inst.AnimInfo);
                var parts = new List<string>();
                foreach (string name in mg.Animations.Keys.OrderBy(x => x))
                {
                    Material m = model.Materials.First(x => x.Name == name);
                    Vector3 d = m.CurrentDiffuse * 255;
                    parts.Add($"{name}=({d.X:0},{d.Y:0},{d.Z:0})");
                }
                Console.WriteLine($"  {beam,-13} anim {id,2}: colour {string.Join(" ", parts)}" +
                    $"{(tg.Animations.Count > 0 ? " | textures on " + string.Join(",", tg.Animations.Keys) : "")}" +
                    $"{(cg.Animations.Count > 0 ? " | uv on " + string.Join(",", cg.Animations.Keys) : "")}");
            }
        }

        // -guntest clips <gun dir> [hunter=Sylux] [arena="MP3 PROVING GROUND"] [animation name (default: all)]: every animation in gun.bin played alone on
        // Prime's clock (30 fps), for judging each one by eye: per animation a folder of 1600x900 frames under
        // extract_out/campaign/gun_clips/<gun>/<animation>/ -- top: MPH's own gun of that hunter (flat, its size and place)
        // | the Prime gun where the device draws it (a match, first person); bottom: the Prime gun from its right side and
        // from the front, cameras fixed on its resting pose. ffmpeg makes the videos.
        // -guntest vanilla [hunter]: the hunter's own MPH first-person gun model (SyluxGun ...) playing each of its 13 gun
        // states (Metadata.GunAnimationIds column 0: the node motion; the per-beam columns only recolour), flat shaded from
        // two fixed sides, as extract_out/campaign/gun_clips/vanilla_<gun>/<NN_state>/NNNN.png; then which states are the
        // same motion (every node matrix, every frame, within 1e-4)
        private static void Vanilla(Hunter hunter)
        {
            W = 800;
            H = 450;
            string gunName = Metadata.HunterModels[hunter][3];
            ModelInstance inst = Read.GetModelInstance(gunName, noCache: true);
            Model model = inst.Model;
            var nodeIdx = model.Nodes.Select((x, i) => (x, i)).ToDictionary(p => p.x, p => p.i);
            var states = Enum.GetValues<GunAnimation>().OrderBy(x => (int)x).ToArray();
            int[] ids = states.Select(st => Metadata.GunAnimationIds[(int)hunter, (int)st, 0]).ToArray();
            // per state: every frame's node matrices and posed triangles (gun model space)
            var nodeMats = new List<Matrix4[]>[states.Length];
            var frames = new List<List<Tri>>[states.Length];
            for (int si = 0; si < states.Length; si++)
            {
                nodeMats[si] = new List<Matrix4[]>();
                frames[si] = new List<List<Tri>>();
                if (ids[si] < 0) continue;
                inst.SetAnimation(ids[si], slot: 0, SetFlags.Node);
                AnimationInfo info = inst.AnimInfo;
                int n = Math.Max(1, info.FrameCount[0]);
                for (int f = 0; f < n; f++)
                {
                    info.Frame[0] = f;
                    model.ComputeNodeMatrices(0);
                    model.AnimateNodes(0, false, Matrix4.Identity, model.Scale, info);
                    model.UpdateMatrixStack();
                    nodeMats[si].Add(model.Nodes.Select(x => x.Animation).ToArray());
                    var tris = new List<Tri>();
                    IReadOnlyList<float> stack = model.MatrixStackValues;
                    foreach (Node node in model.Nodes)
                    {
                        if (!node.Enabled) continue;
                        for (int mi = node.MeshId / 2; mi < node.MeshId / 2 + node.MeshCount; mi++)
                        {
                            float[] v = DsDisplayList.Decode(model, model.Meshes[mi], false);
                            int stride = DsDisplayList.Stride;
                            for (int i = 0; i + 3 * stride <= v.Length; i += 3 * stride)
                            {
                                var t = new Tri();
                                for (int k = 0; k < 3; k++)
                                {
                                    int o = i + k * stride;
                                    Matrix4 m = node.Animation;
                                    if (model.NodeMatrixIds.Count > 0)
                                    {
                                        int sIdx = (int)(v[o + 12] + 0.5f) * 16;
                                        if (sIdx + 15 < stack.Count)
                                        {
                                            m = new Matrix4(stack[sIdx], stack[sIdx + 1], stack[sIdx + 2], stack[sIdx + 3], stack[sIdx + 4], stack[sIdx + 5],
                                                stack[sIdx + 6], stack[sIdx + 7], stack[sIdx + 8], stack[sIdx + 9], stack[sIdx + 10], stack[sIdx + 11],
                                                stack[sIdx + 12], stack[sIdx + 13], stack[sIdx + 14], stack[sIdx + 15]);
                                        }
                                    }
                                    t.V[k] = (new Vector4(v[o], v[o + 1], v[o + 2], 1) * m).Xyz;
                                }
                                // lit by its model-space normal (the raster's screen-space shade flattens a model this small), each
                                // node its own hue so the parts that move stand apart
                                Vector3 fn = Vector3.Cross(t.V[1] - t.V[0], t.V[2] - t.V[0]);
                                float lit = fn.LengthSquared > 1e-20f ? 0.45f + 0.55f * MathF.Abs(Vector3.Dot(fn.Normalized(), new Vector3(0.4f, 0.8f, -0.45f).Normalized())) : 0.6f;
                                float hue = (nodeIdx[node] * 0.618034f) % 1f;
                                Vector3 rgb = new Vector3(0.55f + 0.45f * MathF.Cos(6.2832f * hue), 0.55f + 0.45f * MathF.Cos(6.2832f * (hue - 0.333f)), 0.55f + 0.45f * MathF.Cos(6.2832f * (hue - 0.667f)));
                                t.Colour = new Rgba32((byte)(255 * rgb.X * lit), (byte)(255 * rgb.Y * lit), (byte)(255 * rgb.Z * lit));
                                tris.Add(t);
                            }
                        }
                    }
                    frames[si].Add(tris);
                }
                Console.WriteLine($"  {(int)states[si],2} {states[si],-18} anim {ids[si],2}: {n} frames ({n / 30f:0.00} s at 30 fps)");
            }
            // fixed cameras around the idle pose
            int idle = Array.IndexOf(states, GunAnimation.Idle);
            (Vector3 lo, Vector3 hi) = Bounds(frames[idle][0].SelectMany(t => t.V));
            Vector3 c = (lo + hi) / 2;
            float r = (hi - lo).Length / 2;
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(30), W / (float)H, r * 0.05f, r * 40);
            Matrix4 sideVp = Matrix4.LookAt(c + new Vector3(2.6f * r, 0.5f * r, 0), c, Vector3.UnitY) * proj;
            Matrix4 threeVp = Matrix4.LookAt(c + new Vector3(-1.4f * r, 1.0f * r, -2.2f * r), c, Vector3.UnitY) * proj;
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign", "gun_clips", "vanilla_" + gunName));
            for (int si = 0; si < states.Length; si++)
            {
                if (frames[si].Count == 0) continue;
                string d = Path.Combine(root, $"{si + 1:00}_{states[si]}");
                Directory.CreateDirectory(d);
                int n = frames[si].Count;
                int total = n == 1 ? 60 : n + 15;
                for (int k = 0; k < total; k++)
                {
                    List<Tri> tris = frames[si][Math.Min(k, n - 1)];
                    var img = new Image<Rgba32>(2 * W, H, new Rgba32(22, 24, 28));
                    var panel = new Image<Rgba32>(W, H, new Rgba32(22, 24, 28));
                    Raster(panel, 0, tris, sideVp);
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) img[x, y] = panel[x, y];
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) panel[x, y] = new Rgba32(22, 24, 28);
                    Raster(panel, 0, tris, threeVp);
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) img[W + x, y] = panel[x, y];
                    img.SaveAsPng(Path.Combine(d, $"{k:0000}.png"));
                }
            }
            // which states are the same motion
            Console.WriteLine("  identical motion (every node, every frame, within 1e-4, forwards or reversed):");
            for (int a = 0; a < states.Length; a++)
            {
                for (int b = a + 1; b < states.Length; b++)
                {
                    if (nodeMats[a].Count == 0 || nodeMats[b].Count == 0) continue;
                    string same;
                    if (ids[a] == ids[b]) same = "SAME ANIMATION (same id)";
                    else if (nodeMats[a].Count != nodeMats[b].Count) continue;
                    else
                    {
                        // forwards, and backwards (one state the other played in reverse)
                        int n = nodeMats[a].Count;
                        float fwd = 0, rev = 0;
                        for (int f = 0; f < n; f++)
                            for (int j = 0; j < nodeMats[a][f].Length; j++)
                                for (int q = 0; q < 16; q++)
                                {
                                    fwd = MathF.Max(fwd, MathF.Abs(nodeMats[a][f][j][q / 4, q % 4] - nodeMats[b][f][j][q / 4, q % 4]));
                                    rev = MathF.Max(rev, MathF.Abs(nodeMats[a][f][j][q / 4, q % 4] - nodeMats[b][n - 1 - f][j][q / 4, q % 4]));
                                }
                        if (fwd <= 1e-4f) same = $"identical (max {fwd:0.######})";
                        else if (rev <= 1e-4f) same = $"the same motion in reverse (max {rev:0.######})";
                        else continue;
                    }
                    Console.WriteLine($"    {states[a]} = {states[b]}: {same}");
                }
            }
            // and how far each moves at all (a state that never leaves the idle pose)
            Matrix4[] rest = nodeMats[idle][0];
            for (int si = 0; si < states.Length; si++)
            {
                float max = 0;
                foreach (Matrix4[] fm in nodeMats[si])
                    for (int j = 0; j < fm.Length; j++) max = MathF.Max(max, (fm[j].ExtractTranslation() - rest[j].ExtractTranslation()).Length);
                Console.WriteLine($"    {states[si],-18} furthest any node gets from the idle pose: {max:0.####}");
            }
            Console.WriteLine($"  wrote {root}");
        }

        private static void Clips(string dir, Hunter hunter, string arena, string only)
        {
            W = 800;
            H = 450;
            HdGun gun = HdGun.Load(Path.Combine(dir, "gun.bin"));
            var vm = new GunViewmodel(gun);
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, arena, hunter, 1);
            settings.Players[1].BotLevel = 0;
            using CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
            var input = new CampaignInput();
            int alive = 0;
            for (int i = 0; i < 3000 && !host.Ended && alive < 90; i++)
            {
                host.Step(input);
                alive = host.Player.Health > 0 ? alive + 1 : 0;
            }
            PlayerEntity player = host.Player;
            Scene scene = host.Scene;
            string mphGun = Metadata.HunterModels[player.Hunter][3];
            vm.Update(player, 1 / 60f);
            Vector3 restMuzzle = vm.RestMuzzle();
            Matrix4 model = vm.ModelMatrix(player, gun.Scale, restMuzzle);
            // GUNCLIPS_AIM="yaw pitch [size]": the gun turned about its muzzle (degrees) and resized, for trying a placement
            if (Environment.GetEnvironmentVariable("GUNCLIPS_AIM") is { Length: > 0 } aim)
            {
                float[] v = aim.Split(' ').Select(x => Single.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var m = new Vector3(0, 0, Fixed.ToFloat(player.Values.MuzzleOffset));
                Matrix4 toGun = player.GunDrawTransform.Inverted();
                model = model * toGun * Matrix4.CreateTranslation(-m) * Matrix4.CreateScale(v.Length > 2 ? v[2] : 1)
                    * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(v[1])) * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(v[0]))
                    * Matrix4.CreateTranslation(m) * player.GunDrawTransform;
            }
            Matrix4 viewProj = scene.ViewMatrix * scene.PerspectiveMatrix;
            List<Tri> mph = MphGunTriangles(scene, player, world: true, gunModel: mphGun);
            GunViewmodel.TryMphLightColour(scene, out Vector3 tint, mphGun);
            // the side cameras: around the resting gun, in the player's view axes
            (Vector3 lo, Vector3 hi) = Bounds(SkinnedTriangles(vm, model).SelectMany(t => t.V));
            Vector3 c = (lo + hi) / 2;
            float r = (hi - lo).Length / 2;
            Matrix4 camWorld = scene.ViewMatrix.Inverted();
            Vector3 right = camWorld.Row0.Xyz.Normalized(), up = camWorld.Row1.Xyz.Normalized(), back = camWorld.Row2.Xyz.Normalized();
            Matrix4 sideProj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(30), W / (float)H, r * 0.05f, r * 40);
            Matrix4 sideView = Matrix4.LookAt(c + right * 2.3f * r + up * 0.4f * r, c, up);
            Matrix4 frontView = Matrix4.LookAt(c - back * 1.9f * r - right * 0.9f * r + up * 0.7f * r, c, up);
            // both guns in gun space (MPH's gun matrix undone), for placing a Prime gun over the hunter's own
            {
                Matrix4 toGun = player.GunDrawTransform.Inverted();
                List<Vector3> mg = MphGunTriangles(scene, player, gunModel: mphGun).SelectMany(t => t.V).ToList();
                List<Vector3> pg = SkinnedTriangles(vm, model * toGun).SelectMany(t => t.V).ToList();
                (Vector3 a0, Vector3 a1) = Bounds(mg);
                (Vector3 b0, Vector3 b1) = Bounds(pg);
                Console.WriteLine($"  gun space: MPH {mphGun} min {a0} max {a1}; Prime min {b0} max {b1}; MPH muzzle z {Fixed.ToFloat(player.Values.MuzzleOffset):0.####}");
                foreach (float z in new[] { 0.6f, 0.4f, 0.2f, 0f, -0.2f, -0.4f, -0.6f })
                {
                    List<Vector3> sl = mg.Where(v => MathF.Abs(v.Z - z) < 0.05f).ToList();
                    if (sl.Count == 0) continue;
                    (Vector3 s0, Vector3 s1) = Bounds(sl);
                    Console.WriteLine($"    MPH slice z {z,5:0.0}: x [{s0.X:0.###}, {s1.X:0.###}] y [{s0.Y:0.###}, {s1.Y:0.###}]");
                }
                Console.WriteLine($"    principal axis: MPH {Axis(mg)}, Prime {Axis(pg)}");
                static Vector3 Axis(List<Vector3> pts)
                {
                    Vector3 m = Vector3.Zero;
                    foreach (Vector3 v in pts) m += v;
                    m /= pts.Count;
                    var c = new double[9];
                    foreach (Vector3 v in pts)
                    {
                        Vector3 d = v - m;
                        float[] e = { d.X, d.Y, d.Z };
                        for (int r = 0; r < 9; r++) c[r] += e[r / 3] * e[r % 3];
                    }
                    Vector3 ax = Vector3.UnitZ;
                    for (int i = 0; i < 100; i++) ax = new Vector3((float)(c[0] * ax.X + c[1] * ax.Y + c[2] * ax.Z), (float)(c[3] * ax.X + c[4] * ax.Y + c[5] * ax.Z), (float)(c[6] * ax.X + c[7] * ax.Y + c[8] * ax.Z)).Normalized();
                    return ax.Z < 0 ? -ax : ax;
                }
            }
            Console.WriteLine($"  {player.Hunter} ({mphGun}): {mph.Count} MPH gun tris; Prime gun {gun.BoneCount} bones, scale {gun.Scale}, "
                + $"rest size {hi - lo}; {gun.Anims.Count} animations");
            string outRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign",
                "gun_clips", Path.GetFileName(dir.TrimEnd('\\', '/'))));
            using var gx = new PcGx(gun, dir, W, H);
            var panel = new Image<Rgba32>(W, H);
            foreach (HdGun.Anim a in gun.Anims.Values)
            {
                if (only.Length > 0 && a.Name != only) continue;
                string clipDir = Path.Combine(outRoot, a.Name);
                if (Directory.Exists(clipDir)) Directory.Delete(clipDir, recursive: true);
                Directory.CreateDirectory(clipDir);
                // a held pose: 2 s; a clip: its length, then half a second on its last frame
                int n = a.Keys <= 1 ? 60 : (int)MathF.Ceiling(a.Duration * 30) + 1 + 15;
                for (int k = 0; k < n; k++)
                {
                    float t = MathF.Min(k / 30f, a.Duration);
                    vm.PoseClip(a.Name, t);
                    // GUNCLIPS_BIND=1: every bone at its bind pose instead (the mesh as authored, as the third-person body shows it)
                    // (moved rigidly so the forearm joint sits where the clip puts it)
                    if (Environment.GetEnvironmentVariable("GUNCLIPS_BIND") == "1")
                    {
                        int fa = vm.Gun.BoneIndex("arm_R0_forearm_jnt");
                        Matrix4 move = fa >= 0 ? vm.Gun.InvBind[fa] * vm.World[fa] : Matrix4.Identity;
                        for (int b = 0; b < vm.World.Length; b++) vm.World[b] = vm.Gun.InvBind[b].Inverted() * move;
                    }
                    var img = new Image<Rgba32>(2 * W, 2 * H, new Rgba32(22, 24, 28));
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) panel[x, y] = new Rgba32(22, 24, 28);
                    Raster(panel, 0, mph, viewProj);
                    Crosshair(panel, 0);
                    Blit(panel, img, 0, 0);
                    gx.Render(vm, model, scene.ViewMatrix, scene.PerspectiveMatrix, tint, t, panel, 0);
                    Crosshair(panel, 0);
                    Blit(panel, img, W, 0);
                    gx.Render(vm, model, sideView, sideProj, tint, t, panel, 0);
                    Blit(panel, img, 0, H);
                    gx.Render(vm, model, frontView, sideProj, tint, t, panel, 0);
                    Blit(panel, img, W, H);
                    img.SaveAsPng(Path.Combine(clipDir, $"{k:0000}.png"));
                    img.Dispose();
                }
                Console.WriteLine($"  {a.Name}: {a.Keys} keys, {a.Duration:0.00} s -> {n} frames");
            }
            Console.WriteLine($"  wrote {outRoot}");
            static void Blit(Image<Rgba32> src, Image<Rgba32> dst, int ox, int oy)
            {
                for (int y = 0; y < src.Height; y++) for (int x = 0; x < src.Width; x++) dst[ox + x, oy + y] = src[x, y];
            }
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
        private static List<Tri> MphGunTriangles(Scene scene, PlayerEntity player, bool world = false, string gunModel = "SamusGun")
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
                    if (model.Name != gunModel)
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
