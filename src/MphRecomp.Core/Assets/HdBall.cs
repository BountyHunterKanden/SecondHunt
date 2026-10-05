using System;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace MphRecomp.Assets
{
    // A Samus suit's own MORPH BALL among the HD models: the static model folder "<suit>Ball" beside the suit's own
    // (hd/MP1VariaSuit -> hd/MP1VariaSuitBall; made on the PC by prime_to_trophy.py / mp4_to_trophy.py --static), one
    // .dae + textures + .gx.json like any other import, and a .convert.json saying "static" and the glow the game shows
    // between the ball's halves ("core", r g b 0..1 -- a particle in the games, drawn here as a lit core). Nothing is
    // rigged: the whole ball rolls as one piece. Shared by the animation viewer and the campaign's alt form.
    //   HdBall.For(hdDir, "MP1VariaSuit")   (hdDir = the device's .../files/hd)   -> null when that suit has no ball
    public sealed class HdBall
    {
        public string Id = "", Folder = "", DaePath = "";
        public Vector3? Core;                 // the glow between the halves, or none
        // MPH's own Morph Ball (Samus's alt form): collision radius 2048 / 4096 = 0.5 -> the HD ball is drawn this wide
        public const float GameDiameter = 1.0f;

        public const float CoreRadius = 0.43f;   // the lit core, x the ball's width: inside the shell, seen through the gap

        public static string IdFor(string suit) => suit + "Ball";

        // The ball's whole placement as one matrix (OpenTK row vectors, v' = v * M), from its model's own centre and width:
        // centred, sized to MPH's morph ball, spun about its axle (the model's X -- the split between the halves) by `spin`,
        // turned to face `heading` (the renderer's facing convention: (sin, 0, -cos)), and lifted to rest on the floor.
        public static OpenTK.Mathematics.Matrix4 Placement(OpenTK.Mathematics.Vector3 center, float width, float spin, float heading)
        {
            float k = GameDiameter / width, r = GameDiameter / 2f;
            return OpenTK.Mathematics.Matrix4.CreateTranslation(-center) * OpenTK.Mathematics.Matrix4.CreateScale(k)
                 * OpenTK.Mathematics.Matrix4.CreateRotationX(spin) * OpenTK.Mathematics.Matrix4.CreateRotationY(MathF.PI - heading)
                 * OpenTK.Mathematics.Matrix4.CreateTranslation(0f, r, 0f);
        }

        // The glow between the halves as a GX program: one stage giving out the core colour, unlit
        public static GxMaterial CoreMaterial(Vector3 core)
        {
            var m = new GxMaterial { Name = "ball_core", NumTevStage = 1, NumChans = 1, CullMode = 2 };
            m.Stages = new[] { new GxTevStage { Ca = 15, Cb = 15, Cc = 15, Cd = 14, Cclamp = 1, Aa = 7, Ab = 7, Ac = 7, Ad = 6, Aclamp = 1, TexMap = -1, RasChan = 7, KcSel = 12, KaSel = 28 } };
            m.Konst[0] = new Vector4(core, 1f);
            return m;
        }

        // The core's triangles: a sphere of CoreRadius x width about the ball's centre, outward-facing (counter-clockwise from
        // outside); each corner = position + normal
        public static System.Collections.Generic.List<(Vector3 Pos, Vector3 Normal)> CoreCorners(Vector3 center, float width, int stacks = 12, int slices = 24)
        {
            var list = new System.Collections.Generic.List<(Vector3, Vector3)>();
            float r = width * CoreRadius;
            Vector3 Dir(int i, int j)
            {
                double th = Math.PI * i / stacks, ph = 2 * Math.PI * j / slices;
                return new Vector3((float)(Math.Sin(th) * Math.Cos(ph)), (float)Math.Cos(th), (float)(Math.Sin(th) * Math.Sin(ph)));
            }
            void Add(Vector3 d) => list.Add((center + d * r, d));
            for (int i = 0; i < stacks; i++)
                for (int j = 0; j < slices; j++)
                {
                    Add(Dir(i, j)); Add(Dir(i + 1, j + 1)); Add(Dir(i + 1, j));
                    Add(Dir(i, j)); Add(Dir(i, j + 1)); Add(Dir(i + 1, j + 1));
                }
            return list;
        }

        public static HdBall? For(string hdDir, string suit)
        {
            string id = IdFor(suit), folder = Path.Combine(hdDir, id);
            if (!Directory.Exists(folder)) return null;
            string[] daes = Directory.GetFiles(folder, "*.dae");
            if (daes.Length == 0) return null;
            var b = new HdBall { Id = id, Folder = folder, DaePath = daes[0] };
            try
            {
                string conv = Path.ChangeExtension(daes[0], ".convert.json");
                if (File.Exists(conv))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(conv));
                    if (doc.RootElement.TryGetProperty("core", out var c) && c.GetArrayLength() >= 3)
                        b.Core = new Vector3((float)c[0].GetDouble(), (float)c[1].GetDouble(), (float)c[2].GetDouble());
                }
            }
            catch (Exception) { }   // a ball without its note still rolls, just without the core glow
            return b;
        }
    }
}
