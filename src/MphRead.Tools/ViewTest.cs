using System;
using System.Reflection;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Hud;
using MphRecomp.Campaign;
using MphRecomp.Framing;
using OpenTK.Mathematics;

namespace MphRead
{
    // View framing checks (MphRecomp.Core View/ViewFraming.cs + Scene's HostFramingAspect / HostScreenWarp / HostAiAspect),
    // PC only:
    //   -viewtest        the math: angles per mode on the Odin's 16:9, the Panini map both ways, edge stretch
    //   -viewtest sim    through MphRead's own campaign sim at 1920x1080 (as on the Odin): the projection per mode, the
    //                    reticle going through the warp, bot vision the same at any screen size
    internal static class ViewTest
    {
        private static int _pass, _fail;

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -- " + detail : "")}");
        }

        public static void Run(string[] args)
        {
            if (args.Length >= 2 && args[1] == "sim")
            {
                RunSim();
            }
            else
            {
                RunMath();
            }
            Console.WriteLine($"viewtest: {_pass} passed, {_fail} failed");
        }

        private static float Deg(float rad) => rad * 180 / MathF.PI;
        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;
        private static readonly float TanY78 = MathF.Tan(39 * MathF.PI / 180);
        private const float Wide16x9 = 16f / 9;

        // how much wider a thing looks at the side edge (middle row) than at the centre: the ratio of degrees per output
        // pixel at the centre to degrees per pixel at the edge. `azimuth` maps output x (-1..1, middle row) to degrees.
        private static float EdgeStretch(Func<float, float> azimuth)
        {
            const float e = 1e-3f;
            float centre = (azimuth(e) - azimuth(-e)) / (2 * e);
            float edge = (azimuth(1) - azimuth(1 - 2 * e)) / (2 * e);
            return centre / edge;
        }

        private static void RunMath()
        {
            Console.WriteLine("view framing on 16:9 (MPH's camera: 78 degrees top to bottom):");
            // 1. Wide = MphRead's own: 78 tall, the sides widen with the screen
            float wideTanX = TanY78 * Wide16x9;
            Check("Wide: 110.4 wide, 78 tall", Near(Deg(2 * MathF.Atan(wideTanX)), 110.4f, 0.1f),
                $"{Deg(2 * MathF.Atan(wideTanX)):0.0} x 78.0");
            float dsOnDs = Deg(2 * MathF.Atan(TanY78 * ViewFraming.DsAspect));
            Check("the DS itself (4:3): 94.4 wide", Near(dsOnDs, 94.4f, 0.1f), $"{dsOnDs:0.0}");
            // 2. DS framing on 16:9 (Scene.FramedFov): the DS's side-to-side view kept
            float dsTanY = TanY78 * ViewFraming.DsAspect / Wide16x9;
            float dsTall = Deg(2 * MathF.Atan(dsTanY)), dsWide = Deg(2 * MathF.Atan(dsTanY * Wide16x9));
            Check("DS framing: 94.4 wide, 62.5 tall", Near(dsWide, 94.4f, 0.1f) && Near(dsTall, 62.5f, 0.1f), $"{dsWide:0.0} x {dsTall:0.0}");
            // 2b. Prime (Metroid Prime's 55 degrees, read from its disc) through the same framing hook
            float primeTanY = TanY78 * ViewFraming.PrimeFramingAspect / Wide16x9;
            float primeTall = Deg(2 * MathF.Atan(primeTanY)), primeWide = Deg(2 * MathF.Atan(primeTanY * Wide16x9));
            Check("Prime: 85.6 wide, 55.0 tall", Near(primeWide, 85.6f, 0.1f) && Near(primeTall, 55, 0.05f), $"{primeWide:0.0} x {primeTall:0.0}");

            // 3. Panini: the map both ways, and crop to fit
            foreach (float d in new[] { 1f, 0.5f })
            {
                var warp = new PaniniWarp(wideTanX, TanY78, d);
                float worst = 0;
                for (float y = -1; y <= 1.001f; y += 0.125f)
                {
                    for (float x = -1; x <= 1.001f; x += 0.125f)
                    {
                        var p = new Vector2(x, y);
                        worst = MathF.Max(worst, (warp.RenderToOutput(warp.OutputToRender(p)) - p).Length);
                    }
                }
                Check($"Panini d={d}: output -> render -> output round trip", worst < 1e-4f, $"worst error {worst:0.0e0}");
                Vector2 corner = warp.OutputToRender(new Vector2(1, 1)), side = warp.OutputToRender(new Vector2(1, 0));
                Vector2 top = warp.OutputToRender(new Vector2(0, 1)), centre = warp.OutputToRender(Vector2.Zero);
                Check($"Panini d={d}: output corner = render corner (nothing blank, nothing lost there)",
                    Near(corner.X, 1, 1e-4f) && Near(corner.Y, 1, 1e-4f), $"({corner.X:0.0000}, {corner.Y:0.0000})");
                Check($"Panini d={d}: side of the middle row keeps Wide's 110.4", Near(side.X, 1, 1e-4f) && Near(side.Y, 0, 1e-6f),
                    $"({side.X:0.0000}, {side.Y:0.0000})");
                Check($"Panini d={d}: centre stays the centre", centre.Length < 1e-6f);
                float tallAtCentre = Deg(2 * MathF.Atan(top.Y * TanY78));
                Console.WriteLine($"        top to bottom at the centre {tallAtCentre:0.0} deg; centre magnified {1 / warp.Scale:0.000}x "
                    + $"(render the 3D at {1 / warp.Scale:0.00}x the screen for 1:1 pixels there)");
            }

            // 4. the numbers the owner feels: edge stretch per mode
            float wideStretch = EdgeStretch(x => Deg(MathF.Atan(x * wideTanX)));
            float dsStretch = EdgeStretch(x => Deg(MathF.Atan(x * dsTanY * Wide16x9)));
            float primeStretch = EdgeStretch(x => Deg(MathF.Atan(x * primeTanY * Wide16x9)));
            var p1 = new PaniniWarp(wideTanX, TanY78, 1f);
            var p5 = new PaniniWarp(wideTanX, TanY78, 0.5f);
            float p1Stretch = EdgeStretch(x => Deg(MathF.Atan(p1.OutputToRender(new Vector2(x, 0)).X * wideTanX)));
            float p5Stretch = EdgeStretch(x => Deg(MathF.Atan(p5.OutputToRender(new Vector2(x, 0)).X * wideTanX)));
            Check("edge stretch Wide 3.07x (1 + tan^2 of the half-angle)", Near(wideStretch, 1 + wideTanX * wideTanX, 0.01f), $"{wideStretch:0.00}x");
            Check("edge stretch DS framing 2.17x (the DS's own)", Near(dsStretch, 2.17f, 0.01f), $"{dsStretch:0.00}x");
            Check("edge stretch Prime 1.86x", Near(primeStretch, 1.86f, 0.01f), $"{primeStretch:0.00}x");
            Check("edge stretch Panini under 1.3x", p1Stretch < 1.3f, $"{p1Stretch:0.00}x");
            Check("edge stretch Panini d=0.5 between", p5Stretch > p1Stretch && p5Stretch < dsStretch, $"{p5Stretch:0.00}x");
        }

        private static void RunSim()
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            Cheats.NoRandomEncounters = true;
            using CampaignHost host = CampaignHost.Start("UNIT2_LAND", arriving: false, collectDrawItems: true,
                viewWidth: 1920, viewHeight: 1080);
            Scene scene = host.Scene;
            PlayerEntity p = host.Player;
            FieldInfo reticleField = typeof(PlayerEntity).GetField("_targetCircleInst", BindingFlags.NonPublic | BindingFlags.Instance)!;
            HudObjectInstance reticle = (HudObjectInstance)reticleField.GetValue(p)!;
            void Idle(int frames)
            {
                for (int i = 0; i < frames; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None });
            }
            int waited = 0;
            while (waited < 3000 && (CameraSequence.Current != null || p.Flags1.TestFlag(PlayerFlags1.NoAimInput)))
            {
                Idle(1);
                waited++;
            }
            Idle(30);
            Console.WriteLine($"campaign sim (UNIT2_LAND at 1920x1080), camera live after {waited} frames:");

            (float wide, float tall) Angles(Matrix4 m) => (Deg(2 * MathF.Atan(1 / m.M11)), Deg(2 * MathF.Atan(1 / m.M22)));
            int Items() => scene.OpaqueItems.Count + scene.DecalItems.Count + scene.TranslucentItems.Count;

            // Wide (the defaults)
            Scene.HostFramingAspect = 0;
            Scene.HostScreenWarp = null;
            Idle(2);
            (float w, float t) = Angles(scene.PerspectiveMatrix);
            Check("Wide (hooks off): 110.4 x 78.0", Near(w, 110.4f, 0.1f) && Near(t, 78, 0.1f), $"{w:0.0} x {t:0.0}");
            var wideReticle = new Vector2(reticle.PositionX, reticle.PositionY);
            int wideItems = Items();
            Matrix4 wideProj = scene.PerspectiveMatrix;

            // DS framing
            Scene.HostFramingAspect = ViewFraming.DsAspect;
            Idle(2);
            (w, t) = Angles(scene.PerspectiveMatrix);
            Check("DS framing: 94.4 x 62.5", Near(w, 94.4f, 0.1f) && Near(t, 62.5f, 0.1f), $"{w:0.0} x {t:0.0}");
            int dsItems = Items();
            Check("DS framing: frustum culling follows (no more draw items than Wide)", dsItems <= wideItems, $"{dsItems} vs {wideItems}");
            Matrix4 gun = ViewFraming.VanillaProjection(scene.PerspectiveMatrix, p.CameraInfo.Fov, 1920f / 1080);
            float gunErr = 0;
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) gunErr = MathF.Max(gunErr, MathF.Abs(gun[r, c] - wideProj[r, c]));
            Check("DS framing: the gun's own projection = Wide's (vanilla gun size)", gunErr < 1e-5f, $"max diff {gunErr:0.0e0}");

            Scene.HostFramingAspect = ViewFraming.FramingAspect(ViewMode.Prime);
            Idle(2);
            (w, t) = Angles(scene.PerspectiveMatrix);
            Check("Prime: 85.6 x 55.0", Near(w, 85.6f, 0.1f) && Near(t, 55, 0.05f), $"{w:0.0} x {t:0.0}");
            gun = ViewFraming.VanillaProjection(scene.PerspectiveMatrix, p.CameraInfo.Fov, 1920f / 1080);
            gunErr = 0;
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) gunErr = MathF.Max(gunErr, MathF.Abs(gun[r, c] - wideProj[r, c]));
            Check("Prime: the gun's own projection = Wide's", gunErr < 1e-5f, $"max diff {gunErr:0.0e0}");
            Scene.HostFramingAspect = 0;

            // Panini: the render keeps Wide's projection; the reticle goes through the same warp as the picture
            var warp = PaniniWarp.FromProjection(wideProj, 1f);
            Scene.HostScreenWarp = ndc => PaniniWarp.FromProjection(scene.PerspectiveMatrix, 1f).RenderToOutput(ndc);
            Idle(2);
            (w, t) = Angles(scene.PerspectiveMatrix);
            Check("Panini: the 3D renders with Wide's projection", Near(w, 110.4f, 0.1f) && Near(t, 78, 0.1f), $"{w:0.0} x {t:0.0}");
            var got = new Vector2(reticle.PositionX, reticle.PositionY);
            Vector2 wideNdc = new(wideReticle.X * 2 - 1, 1 - wideReticle.Y * 2);
            Vector2 wantNdc = warp.RenderToOutput(wideNdc);
            var want = new Vector2((wantNdc.X + 1) / 2, (1 - wantNdc.Y) / 2);
            Check("Panini: reticle lands where the warped picture puts its aim point", (got - want).Length < 2e-3f,
                $"Wide ({wideReticle.X:0.0000}, {wideReticle.Y:0.0000}) -> Panini ({got.X:0.0000}, {got.Y:0.0000}), expected ({want.X:0.0000}, {want.Y:0.0000})");
            // off centre (the centre is a fixed point): a locator icon's top-left percentages through the hook
            Vector2 offTopLeft = new(0.85f, 0.2f);
            Vector2 hooked = Scene.HostWarpScreenPos(offTopLeft);
            Vector2 offNdc = warp.RenderToOutput(new Vector2(0.85f * 2 - 1, 1 - 0.2f * 2));
            Vector2 offWant = new((offNdc.X + 1) / 2, (1 - offNdc.Y) / 2);
            Check("Panini: an off-centre marker (0.85, 0.20) moves with the picture", (hooked - offWant).Length < 1e-5f
                && (hooked - offTopLeft).Length > 0.01f,
                $"-> ({hooked.X:0.0000}, {hooked.Y:0.0000}) (the magnified centre pushes it outward)");
            Scene.HostScreenWarp = null;
            Check("hooks off: markers untouched", Scene.HostWarpScreenPos(offTopLeft) == offTopLeft);

            // bot vision: a fixed aspect, whatever the screen
            float fov = MathHelper.DegreesToRadians(78);
            Matrix4 aiOdin = scene.GetAiPerspectiveMatrix(fov);
            Vector2i size = scene.Size;
            scene.Size = new Vector2i(2340, 1080); // the S23's 19.5:9
            Matrix4 aiPhone = scene.GetAiPerspectiveMatrix(fov);
            Matrix4 screenPhone = scene.GetPerspectiveMatrix(fov);
            scene.Size = size;
            (float aiW, _) = Angles(aiOdin);
            Check("bot vision: 110.4 wide on the Odin", Near(aiW, 110.4f, 0.1f), $"{aiW:0.0}");
            Check("bot vision: the same on a 19.5:9 phone", aiOdin == aiPhone,
                $"(that phone's own screen would have given them {Angles(screenPhone).wide:0.0})");
        }
    }
}
