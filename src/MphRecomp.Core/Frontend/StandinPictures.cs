using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using MphRecomp.Render;
using OpenTK.Mathematics;

// The character pictures of the skins with no HD import (owner 2026-10-27: "can we render a picture for all stand ins?
// The weavel icon recolors look bad"): the trooper look-alikes (Weavel's body in the look's colours, LookAlikes) and
// LOW-POLY DARK SAMUS (DarkSamusStandin, in her float). The game's own DS model, posed in the menu's idle frame by
// MphRead's code, lit as MphRead's shader lights a model, and drawn on the CPU into the same two 256 x 384 pictures the
// HD bodies get (CharacterPictures: the full body, and the head close-up the round icon is cut from). Pure: the caller
// hands over a private copy of the model (Read.GetModelInstance noCache), so it runs on a worker thread.
namespace MphRecomp.Frontend
{
    public static class StandinPictures
    {
        // in the pictures' names (CharacterPictures' stem): a new framing or lighting draws them again
        public const string Stamp = "standin1";

        // the stand-in skins: the model each is drawn from, its colours and its pose
        public static (string Model, Hunter Rig, LookAlikes.Look Look, bool Float)? Of(string? skin) => skin?.ToLowerInvariant() switch
        {
            "mp1powertrooper" => ("Weavel_lod0", Hunter.Weavel, LookAlikes.Look.PowerTrooper, false),
            "mp1icetrooper" => ("Weavel_lod0", Hunter.Weavel, LookAlikes.Look.IceTrooper, false),
            "mp1wavetrooper" => ("Weavel_lod0", Hunter.Weavel, LookAlikes.Look.WaveTrooper, false),
            "mp1plasmatrooper" => ("Weavel_lod0", Hunter.Weavel, LookAlikes.Look.PlasmaTrooper, false),
            "darksamusstandin" => (DarkSamusStandin.ModelNames[0], Hunter.Samus, LookAlikes.Look.None, true),
            _ => null,
        };

        public static (Vector3 Center, float Radius, Vector3 Min, Vector3 Max) LastHead; // for the PC test's print

        // the lift's floor (CharacterPictures.Lift): the full lift turns the low-poly Dark Samus vivid purple; this keeps her
        // dark navy with her cyan glows (the troopers' own lift stays above it)
        public const float LiftFloor = 0.7f;

        private const int Super = 3; // samples per side of a picture pixel
        private const float HeadScale = 1.6f; // the head close-up's size over the head joint's own mesh
        private const float FovY = 30;

        // a stand-in skin's model, a private copy to pose (MphRead's loader isn't thread-safe: call it on the thread that
        // loads models), or null: not a stand-in, or the low-poly Dark Samus doesn't build from this ROM (her seat keeps
        // the LookAlikes recolour and its icon)
        public static Model? Load(string skin)
        {
            if (Of(skin) is not { } of || (of.Float && !DarkSamusStandin.Available)) return null;
            try
            {
                return Read.GetModelInstance(of.Model, false, MetaDir.Models, noCache: true).Model;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("stand-in picture: " + skin + ": " + ex.Message);
                return null;
            }
        }

        // both pictures of a stand-in skin from its Load copy, lifted (any thread)
        public static (byte[] Full, byte[] Head) Draw(Model model, string skin)
        {
            var of = Of(skin)!.Value;
            (byte[] full, byte[] head) = Render(model, of.Rig, of.Look, of.Float, (int)PlayerAnimation.Idle, 0);
            CharacterPictures.Lift(full, LiftFloor);
            CharacterPictures.Lift(head, LiftFloor);
            return (full, head);
        }

        // a seat colour's pictures (the colour picker, owner 2026-10-29): any DS body (a stand-in's or a vanilla hunter's
        // own, a private Load-style copy) posed on its rig in recolour `recolor`, lifted as the stand-ins are (any thread)
        public static (byte[] Full, byte[] Head) DrawColour(Model model, Hunter rig, bool floatPose, int recolor)
        {
            (byte[] full, byte[] head) = Render(model, rig, LookAlikes.Look.None, floatPose, (int)PlayerAnimation.Idle, 0, recolor);
            CharacterPictures.Lift(full, LiftFloor);
            CharacterPictures.Lift(head, LiftFloor);
            return (full, head);
        }

        // ---- the menus' backdrop from a DS body (owner 2026-10-29: LOW-POLY DARK SAMUS picked as menu art fell back to the
        // HD Samus; "pose all the hunters too the same way"): MenuSamus's HD framing -- straight on, visor on the top
        // screen, chest on the touch screen -- on the game's own DS model in its idle, drawn by this rasterizer ----

        // in the menu art's cache name: a new framing draws them again
        public const string MenuArtStamp = "ds3";
        // the menu-art ids of the DS bodies (RecompSettings.MenuArtSuit): the low-poly Dark Samus, and "ds:<hunter>"
        public const string MenuArtPrefix = "ds:";
        public static readonly Hunter[] MenuArtHunters =
            { Hunter.Kanden, Hunter.Spire, Hunter.Trace, Hunter.Noxus, Hunter.Sylux, Hunter.Weavel }; // not Samus: ORIGINAL is hers

        public static string MenuArtId(Hunter hunter) => MenuArtPrefix + hunter;

        // a "ds:<hunter>" id's hunter, else null
        public static Hunter? MenuArtHunter(string? id) =>
            MenuArtOf(id) != null && id!.StartsWith(MenuArtPrefix, StringComparison.OrdinalIgnoreCase) ? MenuArtOf(id)!.Value.Rig : null;

        // a DS body's name in the chooser ("KANDEN") and RECOMP SETTINGS' short value ("DS" / "KAN"), else null
        public static string? MenuArtLabel(string? id) => MenuArtHunter(id) is { } h ? h.ToString().ToUpperInvariant()
            : id != null && id.Equals(Characters.DarkSamusStandinSkin, StringComparison.OrdinalIgnoreCase) ? Characters.Label(id) : null;

        public static string? MenuArtValue(string? id) => MenuArtHunter(id) is { } h ? "DS " + h.ToString()[..3].ToUpperInvariant()
            : id != null && id.Equals(Characters.DarkSamusStandinSkin, StringComparison.OrdinalIgnoreCase) ? "LOW DSA" : null;

        // a menu-art id's DS model and rig, or null: not a DS body (an HD suit's id). The low-poly Dark Samus stands here
        // (her float turns her body aside; standing she faces the camera as the DS art's Samus does)
        public static (string Model, Hunter Rig)? MenuArtOf(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.Equals(Characters.DarkSamusStandinSkin, StringComparison.OrdinalIgnoreCase))
            {
                return DarkSamusStandin.Available ? (DarkSamusStandin.ModelNames[0], Hunter.Samus) : null;
            }
            if (!id.StartsWith(MenuArtPrefix, StringComparison.OrdinalIgnoreCase)
                || !Enum.TryParse(id[MenuArtPrefix.Length..], ignoreCase: true, out Hunter h) || Array.IndexOf(MenuArtHunters, h) < 0)
            {
                return null;
            }
            return (Metadata.HunterModels[h][0], h);
        }

        // a menu-art id's model, a private copy (the model-loading thread, as Load), or null
        public static Model? LoadMenuArt(string id)
        {
            if (MenuArtOf(id) is not { } of) return null;
            try
            {
                return Read.GetModelInstance(of.Model, false, MetaDir.Models, noCache: true).Model;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("menu art: " + id + ": " + ex.Message);
                return null;
            }
        }

        // the share of the body's height the picture shows, and where the head's middle sits (from the top): head and
        // shoulders on the top screen, the chest down to the hips on the touch screen (tuned by eye on all eight bodies
        // with -menuartds; raised from 0.26 to HD Samus's head height, owner 2026-10-29 "way too low")
        public const float MenuArtSpan = 0.5f;
        public const float MenuArtHeadAt = 0.12f;

        // a body raised in the picture (a share of its height) where its head joint isn't its face: Spire's face sits low
        // on his body, under the crest the joint finds (owner 2026-10-29: "spire should be raised so his face is more level
        // with everyone")
        private static float MenuArtRaise(string id) => MenuArtHunter(id) == Hunter.Spire ? 0.2f : 0;

        // the backdrop, outW x outH (MenuSamus.Aspect), RGBA top row first, opaque on black, lifted (any thread); drawn
        // `super` x `super` larger and averaged down
        public static byte[] MenuArt(Model model, string id, int outW, int outH, int super = 2, int clip = (int)PlayerAnimation.Idle,
            float spanShare = MenuArtSpan, float headAt = MenuArtHeadAt)
        {
            var of = MenuArtOf(id)!.Value;
            Pose(model, of.Rig, clip, 0, false);
            List<(Vector3 P, Vector3 N, Vector2 T, Vector3 C, bool DifAmb, int Material, int Node)> verts = Vertices(model);
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var v in verts)
            {
                min = Vector3.ComponentMin(min, v.P);
                max = Vector3.ComponentMax(max, v.P);
            }
            (Vector3 head, float radius) = Head(model, verts, min, max);
            LastHead = (head, radius, min, max);
            float span = (max.Y - min.Y) * spanShare;
            float midY = head.Y - span * (0.5f - headAt + MenuArtRaise(id));
            // across: the middle of what the picture shows (the median across of the corners in its band), not the head's
            // (owner 2026-10-29: Spire leans, his head off his body's middle)
            var across = new List<float>();
            foreach (var v in verts)
            {
                if (MathF.Abs(v.P.Y - midY) <= span / 2) across.Add(v.P.X);
            }
            across.Sort();
            float midX = across.Count > 0 ? across[across.Count / 2] : head.X;
            Vector3 target = new(midX, midY, head.Z);
            float dist = span / 2 / MathF.Tan(FovY * MathF.PI / 360f);
            byte[] px = Draw(model, LookAlikes.Look.None, 0, verts, target - Vector3.UnitZ * dist, target, outW, outH, super);
            CharacterPictures.Lift(px, LiftFloor);
            for (int i = 0; i < px.Length; i += 4)
            {
                // over black: the colour times its coverage
                int a = px[i + 3];
                px[i] = (byte)(px[i] * a / 255);
                px[i + 1] = (byte)(px[i + 1] * a / 255);
                px[i + 2] = (byte)(px[i + 2] * a / 255);
                px[i + 3] = 255;
            }
            return px;
        }

        private struct Tri
        {
            public Vector3 P0, P1, P2; // world
            public Vector3 N0, N1, N2; // view space
            public Vector2 T0, T1, T2;
            public Vector3 C0, C1, C2; // the vertex colour (diffuse source when DifAmb)
            public bool DifAmb;
            public int Material;
        }

        private sealed class Tex
        {
            public int W, H;
            public byte[] Rgba = Array.Empty<byte>();
            public RepeatMode WrapS, WrapT;
        }

        // the full picture and the head close-up, RGBA top row first (alpha = the body), not yet lifted
        public static (byte[] Full, byte[] Head) Render(Model model, Hunter rig, LookAlikes.Look look, bool floatPose,
            int clip, int frame, int recolor = 0)
        {
            Pose(model, rig, clip, frame, floatPose);
            List<(Vector3 P, Vector3 N, Vector2 T, Vector3 C, bool DifAmb, int Material, int Node)> verts = Vertices(model);
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var v in verts)
            {
                min = Vector3.ComponentMin(min, v.P);
                max = Vector3.ComponentMax(max, v.P);
            }
            // the full body: from the HD pictures' side (front, a little to its right), the whole height in frame
            Vector3 mid = (min + max) / 2;
            float aspect = CharacterPictures.FullW / (float)CharacterPictures.FullH;
            float half = MathF.Max(max.Y - min.Y, MathF.Max(max.X - min.X, max.Z - min.Z) / aspect) / 2 * 1.08f;
            Vector3 dir = new Vector3(-1.6f, 0.02f, -5.4f).Normalized();
            float dist = half / MathF.Tan(FovY * MathF.PI / 360f);
            byte[] full = Draw(model, look, recolor, verts, mid + dir * dist, mid, CharacterPictures.FullW, CharacterPictures.FullH);
            (Vector3 center, float radius) = Head(model, verts, min, max);
            LastHead = (center, radius, min, max);
            (Vector3 eye, Vector3 target) = CharacterPictures.HeadCamera(center - Vector3.UnitY * radius * 0.2f, -Vector3.UnitZ, radius, FovY);
            byte[] head = Draw(model, look, recolor, verts, eye, target, CharacterPictures.HeadW, CharacterPictures.HeadH);
            return (full, head);
        }

        // MphRead's own posing (MenuWidgets.Evaluate, PlayerDraw's biped): materials at rest, the clip's frame on every node,
        // her float over it, then the body stood in the rig's space as the HD bodies are (faces -Z, feet on y = 0)
        private static void Pose(Model model, Hunter rig, int clip, int frame, bool floatPose)
        {
            var info = new AnimationInfo();
            model.AnimateMaterials(info);
            model.AnimateTextures(info);
            var groups = model.AnimationGroups;
            info.Node.Group = clip >= 0 && clip < groups.Node.Count ? groups.Node[clip] : null;
            info.Index[0] = clip;
            info.FrameCount[0] = info.Node.Group?.FrameCount ?? 1;
            info.Frame[0] = Math.Clamp(frame, 0, Math.Max(0, info.FrameCount[0] - 1));
            model.AnimateNodes(index: 0, false, Matrix4.Identity, Vector3.One, info);
            if (floatPose) PlayerEntity.ApplyHostFloatPose(model, 1, 0, Vector4.Zero, 0);
            // at the hunter's scale; the DS bodies already face -Z in their own space (PlayerDraw turns them to the aim)
            float scale = Metadata.HunterScales[rig];
            Matrix4 place = Matrix4.Identity;
            place.Row0.Xyz = new Vector3(1, 0, 0) * scale;
            place.Row1.Xyz = new Vector3(0, 1, 0) * scale;
            place.Row2.Xyz = new Vector3(0, 0, 1) * scale;
            foreach (Node node in model.Nodes) node.Animation *= place;
            model.UpdateMatrixStack();
        }

        private static List<(Vector3 P, Vector3 N, Vector2 T, Vector3 C, bool DifAmb, int Material, int Node)> Vertices(Model model)
        {
            var output = new List<(Vector3, Vector3, Vector2, Vector3, bool, int, int)>();
            var meshes = new float[model.Meshes.Count][];
            for (int i = 0; i < model.Nodes.Count && model.Nodes.Count > 0;)
            {
                Walk(0);
                break;
            }
            float minY = float.MaxValue;
            foreach (var v in output) minY = MathF.Min(minY, v.Item1.Y);
            for (int i = 0; i < output.Count; i++)
            {
                var v = output[i];
                v.Item1.Y -= minY; // feet on y = 0
                output[i] = v;
            }
            return output;

            void Walk(int index)
            {
                for (int i = index; i != -1;)
                {
                    Node node = model.Nodes[i];
                    if (node.Enabled)
                    {
                        int start = node.MeshId / 2;
                        for (int k = 0; k < node.MeshCount; k++) Emit(start + k, node, i);
                        if (node.ChildIndex != -1) Walk(node.ChildIndex);
                    }
                    i = node.NextIndex;
                }
            }

            void Emit(int meshId, Node node, int nodeIndex)
            {
                Mesh mesh = model.Meshes[meshId];
                if (!mesh.Visible) return;
                Material material = model.Materials[mesh.MaterialId];
                if (material.CurrentAlpha <= 0) return;
                float[] list = meshes[meshId] ??= DsDisplayList.Decode(model, mesh, isRoom: false);
                Vector3 diffuse = material.CurrentDiffuse;
                const int stride = DsDisplayList.Stride;
                for (int o = 0; o + stride <= list.Length; o += stride)
                {
                    int stack = (int)list[o + 12];
                    bool stacked = model.NodeMatrixIds.Count > 0;
                    Matrix4 m = stacked ? StackMatrix(model, stack) : node.Animation;
                    int owner = stacked && stack < model.NodeMatrixIds.Count ? model.NodeMatrixIds[stack] : nodeIndex;
                    Vector3 p = (new Vector4(list[o], list[o + 1], list[o + 2], 1) * m).Xyz;
                    Vector3 n = (new Vector4(list[o + 3], list[o + 4], list[o + 5], 0) * m).Xyz;
                    n = n.LengthSquared > 1e-12f ? n.Normalized() : Vector3.UnitZ;
                    float ca = list[o + 9];
                    Vector3 c = ca == DsDisplayList.MaterialColor ? diffuse : new Vector3(list[o + 6], list[o + 7], list[o + 8]);
                    output.Add((p, n, new Vector2(list[o + 10], list[o + 11]), c, ca == 0, mesh.MaterialId, owner));
                }
            }
        }

        private static Matrix4 StackMatrix(Model model, int index)
        {
            IReadOnlyList<float> s = model.MatrixStackValues;
            int o = index * 16;
            return new Matrix4(s[o], s[o + 1], s[o + 2], s[o + 3], s[o + 4], s[o + 5], s[o + 6], s[o + 7],
                s[o + 8], s[o + 9], s[o + 10], s[o + 11], s[o + 12], s[o + 13], s[o + 14], s[o + 15]);
        }

        // the head's middle and size: the vertices on the head joint (MPH's Head_1, else the node named head), else the top
        // of the body
        private static (Vector3 Center, float Radius) Head(Model model,
            List<(Vector3 P, Vector3 N, Vector2 T, Vector3 C, bool DifAmb, int Material, int Node)> verts, Vector3 min, Vector3 max)
        {
            int head = -1;
            for (int i = 0; i < model.Nodes.Count && head < 0; i++) if (model.Nodes[i].Name == "Head_1") head = i;
            for (int i = 0; i < model.Nodes.Count && head < 0; i++)
            {
                if (model.Nodes[i].Name.Contains("head", StringComparison.OrdinalIgnoreCase)) head = i;
            }
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            int count = 0;
            foreach (var v in verts)
            {
                if (v.Node != head) continue;
                lo = Vector3.ComponentMin(lo, v.P);
                hi = Vector3.ComponentMax(hi, v.P);
                count++;
            }
            if (count < 8)
            {
                float h = max.Y - min.Y;
                return (new Vector3((min.X + max.X) / 2, max.Y - h * 0.08f, (min.Z + max.Z) / 2), h * 0.09f);
            }
            // the head joint's own vertices are about the helmet's front; the HD heads' size (95th percentile of the whole
            // head) is about twice that
            Vector3 size = hi - lo;
            return ((lo + hi) / 2, MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / 2 * HeadScale);
        }

        // ---- the CPU rasterizer: MphRead's vertex lighting (SceneShaderSource lightShare, two lights in view space),
        // its texture combine (modulate / decal), the solid pass then the see-through pass over it (no depth writes),
        // nearest texels with the material's wrap, drawn Super x Super times larger and averaged down ----

        private static readonly Vector3 Light0Dir = new Vector3(0.45f, -0.55f, -0.7f).Normalized();
        private static readonly Vector3 Light0Color = new(1f, 1f, 1f);
        private static readonly Vector3 Light1Dir = new Vector3(-0.6f, 0.2f, -0.75f).Normalized();
        private static readonly Vector3 Light1Color = new(0.45f, 0.45f, 0.5f);

        private static byte[] Draw(Model model, LookAlikes.Look look, int recolor,
            List<(Vector3 P, Vector3 N, Vector2 T, Vector3 C, bool DifAmb, int Material, int Node)> verts,
            Vector3 eye, Vector3 target, int outW, int outH, int super = Super)
        {
            int w = outW * super, h = outH * super;
            Matrix4 view = Matrix4.LookAt(eye, target, Vector3.UnitY);
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(FovY * MathF.PI / 180f, outW / (float)outH, 0.05f, 100f);
            Matrix4 viewProj = view * proj;
            var color = new float[w * h * 4];
            var depth = new float[w * h];
            Array.Fill(depth, float.MaxValue);
            var textures = new Dictionary<(int, int), Tex?>();
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i + 2 < verts.Count; i += 3)
                {
                    Material material = model.Materials[verts[i].Material];
                    Tex? tex = null;
                    if (material.CurrentTextureId >= 0)
                    {
                        var key = (material.CurrentTextureId, material.CurrentPaletteId);
                        if (!textures.TryGetValue(key, out tex)) textures[key] = tex = Texture(model, material, look, recolor);
                    }
                    Triangle(material, tex, verts[i], verts[i + 1], verts[i + 2], view, viewProj, w, h, color, depth, pass);
                }
            }
            // averaged down, the colour weighted by its coverage
            var px = new byte[outW * outH * 4];
            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int sy = 0; sy < super; sy++)
                    {
                        for (int sx = 0; sx < super; sx++)
                        {
                            int o = ((y * super + sy) * w + x * super + sx) * 4;
                            float sa = color[o + 3];
                            r += color[o] * sa; g += color[o + 1] * sa; b += color[o + 2] * sa; a += sa;
                        }
                    }
                    int p = (y * outW + x) * 4;
                    if (a <= 0) continue;
                    px[p] = (byte)Math.Clamp(r / a * 255 + 0.5f, 0, 255);
                    px[p + 1] = (byte)Math.Clamp(g / a * 255 + 0.5f, 0, 255);
                    px[p + 2] = (byte)Math.Clamp(b / a * 255 + 0.5f, 0, 255);
                    px[p + 3] = (byte)Math.Clamp(a / (super * super) * 255 + 0.5f, 0, 255);
                }
            }
            return px;
        }

        // the texture's texels (recolour 0), through the look's colour map for a trooper (LookAlikes.MapColor, what its
        // recolour does to every palette entry and direct colour); another recolour (a seat colour's: the model's own, or one
        // LookAlikes / HunterColours built) as it is
        private static Tex? Texture(Model model, Material material, LookAlikes.Look look, int recolor)
        {
            try
            {
                if (recolor < 0 || recolor >= model.Recolors.Count) recolor = 0;
                if (recolor != 0) look = LookAlikes.Look.None;
                Texture texture = model.Recolors[recolor].Textures[material.CurrentTextureId];
                IReadOnlyList<ColorRgba> pixels = model.GetPixels(material.CurrentTextureId, material.CurrentPaletteId, recolorId: recolor);
                var tex = new Tex { W = texture.Width, H = texture.Height, WrapS = material.XRepeat, WrapT = material.YRepeat };
                tex.Rgba = new byte[tex.W * tex.H * 4];
                for (int i = 0; i < pixels.Count && i * 4 + 3 < tex.Rgba.Length; i++)
                {
                    ColorRgba c = pixels[i];
                    byte r = c.Red, g = c.Green, b = c.Blue;
                    if (look != LookAlikes.Look.None)
                    {
                        int r5 = (r * 31 + 127) / 255, g5 = (g * 31 + 127) / 255, b5 = (b * 31 + 127) / 255;
                        ushort m = LookAlikes.MapColor((ushort)(r5 | g5 << 5 | b5 << 10), look);
                        r = (byte)((m & 31) * 255 / 31);
                        g = (byte)((m >> 5 & 31) * 255 / 31);
                        b = (byte)((m >> 10 & 31) * 255 / 31);
                    }
                    tex.Rgba[i * 4] = r;
                    tex.Rgba[i * 4 + 1] = g;
                    tex.Rgba[i * 4 + 2] = b;
                    tex.Rgba[i * 4 + 3] = c.Alpha;
                }
                return tex;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static Vector3 Lit(Material material, Vector3 n, Vector3 vertex, bool difAmb)
        {
            if (material.Lighting == 0) return vertex;
            Vector3 diffuse = difAmb ? vertex : material.CurrentDiffuse;
            Vector3 ambient = difAmb ? Vector3.Zero : material.CurrentAmbient;
            Vector3 c = Share(Light0Dir, Light0Color, n, diffuse, ambient, material.CurrentSpecular)
                + Share(Light1Dir, Light1Color, n, diffuse, ambient, material.CurrentSpecular);
            return Vector3.ComponentMin(c, Vector3.One);
        }

        private static Vector3 Share(Vector3 dir, Vector3 light, Vector3 n, Vector3 diffuse, Vector3 ambient, Vector3 specular)
        {
            float amount = MathF.Max(0, -Vector3.Dot(dir, n));
            Vector3 halfway = (dir + new Vector3(0, 0, -1)) / 2;
            float shine = MathF.Max(0, Vector3.Dot(-halfway, n));
            shine *= shine;
            return specular * light * shine + diffuse * light * amount + ambient * light;
        }

        private static void Triangle(Material material, Tex? tex,
            (Vector3 P, Vector3 N, Vector2 T, Vector3 C, bool DifAmb, int Material, int Node) a,
            (Vector3 P, Vector3 N, Vector2 T, Vector3 C, bool DifAmb, int Material, int Node) b,
            (Vector3 P, Vector3 N, Vector2 T, Vector3 C, bool DifAmb, int Material, int Node) c,
            Matrix4 view, Matrix4 viewProj, int w, int h, float[] color, float[] depth, int pass)
        {
            Vector4 ca = new Vector4(a.P, 1) * viewProj, cb = new Vector4(b.P, 1) * viewProj, cc = new Vector4(c.P, 1) * viewProj;
            if (ca.W <= 0.01f || cb.W <= 0.01f || cc.W <= 0.01f) return;
            Vector3 sa = Screen(ca, w, h), sb = Screen(cb, w, h), sc = Screen(cc, w, h);
            float area = (sb.X - sa.X) * (sc.Y - sa.Y) - (sb.Y - sa.Y) * (sc.X - sa.X);
            if (MathF.Abs(area) < 1e-6f) return;
            bool front = area < 0; // counter-clockwise on screen (y down here)
            if (material.Culling == CullingMode.Back && !front || material.Culling == CullingMode.Front && front) return;
            // the vertex stage: lit colours (normals into view space), texture coordinates (normal-generated: the view normal)
            Vector3 Nv(Vector3 n) => (new Vector4(n, 0) * view).Xyz.Normalized();
            Vector3 na = Nv(a.N), nb = Nv(b.N), nc = Nv(c.N);
            Vector3 la = Lit(material, na, a.C, a.DifAmb), lb = Lit(material, nb, b.C, b.DifAmb), lc = Lit(material, nc, c.C, c.DifAmb);
            Vector2 ta = a.T, tb = b.T, tc = c.T;
            if (material.TexgenMode == TexgenMode.Normal)
            {
                ta = new Vector2(na.X * 0.5f + 0.5f, -na.Y * 0.5f + 0.5f);
                tb = new Vector2(nb.X * 0.5f + 0.5f, -nb.Y * 0.5f + 0.5f);
                tc = new Vector2(nc.X * 0.5f + 0.5f, -nc.Y * 0.5f + 0.5f);
            }
            float matAlpha = material.CurrentAlpha;
            float wa = 1 / ca.W, wb = 1 / cb.W, wc = 1 / cc.W;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.X, MathF.Min(sb.X, sc.X))));
            int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(sa.X, MathF.Max(sb.X, sc.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y))));
            int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y))));
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f;
                    float e0 = ((sb.X - px) * (sc.Y - py) - (sb.Y - py) * (sc.X - px)) / area;
                    float e1 = ((sc.X - px) * (sa.Y - py) - (sc.Y - py) * (sa.X - px)) / area;
                    float e2 = 1 - e0 - e1;
                    if (e0 < 0 || e1 < 0 || e2 < 0) continue;
                    float z = e0 * sa.Z + e1 * sb.Z + e2 * sc.Z;
                    int i = y * w + x;
                    if (z >= depth[i]) continue;
                    // perspective-correct weights
                    float q0 = e0 * wa, q1 = e1 * wb, q2 = e2 * wc, qs = q0 + q1 + q2;
                    q0 /= qs; q1 /= qs; q2 /= qs;
                    Vector3 lit = la * q0 + lb * q1 + lc * q2;
                    float r = lit.X, g = lit.Y, bl = lit.Z, al = matAlpha;
                    if (tex != null)
                    {
                        Vector2 uv = ta * q0 + tb * q1 + tc * q2;
                        int tx = Wrap((int)MathF.Floor(uv.X * tex.W), tex.W, tex.WrapS);
                        int ty = Wrap((int)MathF.Floor(uv.Y * tex.H), tex.H, tex.WrapT);
                        int t = (ty * tex.W + tx) * 4;
                        float tr = tex.Rgba[t] / 255f, tg = tex.Rgba[t + 1] / 255f, tb2 = tex.Rgba[t + 2] / 255f, tav = tex.Rgba[t + 3] / 255f;
                        if (material.PolygonMode == PolygonMode.Decal)
                        {
                            r = tr * tav + r * (1 - tav); g = tg * tav + g * (1 - tav); bl = tb2 * tav + bl * (1 - tav);
                        }
                        else
                        {
                            r *= tr; g *= tg; bl *= tb2; al *= tav;
                        }
                    }
                    bool solid = al >= 0.99f;
                    if (pass == 0)
                    {
                        if (!solid) continue;
                        depth[i] = z;
                        color[i * 4] = r; color[i * 4 + 1] = g; color[i * 4 + 2] = bl; color[i * 4 + 3] = 1;
                    }
                    else
                    {
                        if (solid || al <= 0) continue;
                        float da = color[i * 4 + 3], oa = al + da * (1 - al);
                        if (oa <= 0) continue;
                        color[i * 4] = (r * al + color[i * 4] * da * (1 - al)) / oa;
                        color[i * 4 + 1] = (g * al + color[i * 4 + 1] * da * (1 - al)) / oa;
                        color[i * 4 + 2] = (bl * al + color[i * 4 + 2] * da * (1 - al)) / oa;
                        color[i * 4 + 3] = oa;
                    }
                }
            }
        }

        private static Vector3 Screen(Vector4 clip, int w, int h) =>
            new((clip.X / clip.W * 0.5f + 0.5f) * w, (0.5f - clip.Y / clip.W * 0.5f) * h, clip.Z / clip.W);

        private static int Wrap(int i, int size, RepeatMode mode)
        {
            if (mode == RepeatMode.Repeat) return ((i % size) + size) % size;
            if (mode == RepeatMode.Mirror)
            {
                int p = ((i % (2 * size)) + 2 * size) % (2 * size);
                return p < size ? p : 2 * size - 1 - p;
            }
            return Math.Clamp(i, 0, size - 1);
        }
    }
}
