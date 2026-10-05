using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats;
using MphRecomp.Render;
using OpenTK.Mathematics;

// Evaluates the ROM's menu widget models (an MPH model + one animation file per state) at a given frame and turns
// them into flat, screen-facing triangles in MENU SPACE: DS pixels, X right, Y UP, origin on the seam between the
// two screens (top screen y 0..192, touch screen y -192..0). Animation runs through MphRead's own code in the same
// order its entities use (materials, textures, node matrices, node animation, matrix stack, texcoord animation), so a
// widget looks exactly as the model data says; each animation group plays at its own length.
namespace MphRecomp.Frontend
{
    public struct WidgetTri
    {
        public UiVertex A, B, C; // X/Y in menu space
        public float Z; // larger = nearer (drawn later)
        public int Item; // the menu item it belongs to (the layout moves whole items)
        public int TextureId;
        public UiWrap WrapS, WrapT;
    }

    public sealed class MenuWidgets
    {
        private readonly MenuFile _file;
        private readonly UiTextureCache _textures;
        private readonly Dictionary<string, ModelInstance> _instances = new();
        private readonly Dictionary<ModelInstance, List<float[]>> _meshes = new();

        public MenuWidgets(MenuFile file, UiTextureCache textures)
        {
            _file = file;
            _textures = textures;
        }

        // the game's paths use backslashes; MphRead combines them with the file system root itself
        private static string FixPath(string path) => path.Replace('\\', '/');

        public ModelInstance Instance(int widgetIndex)
        {
            MenuWidget w = _file.Widgets[widgetIndex];
            return Instance(w.ModelPath, w.AnimPath);
        }

        // a model by path: a widget's, or one the game's code draws itself (frontend2d/slots and lines, the grooves)
        public ModelInstance Instance(string modelPath, string? animPath)
        {
            string key = modelPath + "|" + animPath;
            if (!_instances.TryGetValue(key, out ModelInstance? inst))
            {
                Model model = Read.ReadModelFile(key, FixPath(modelPath), animPath == null ? null : FixPath(animPath));
                inst = new ModelInstance(model);
                _instances[key] = inst;
            }
            return inst;
        }

        // the longest of the widget animation's groups, in frames (1 for a still)
        public int FrameCount(int widgetIndex) => FrameCount(Instance(widgetIndex));

        public static int FrameCount(ModelInstance inst)
        {
            AnimationGroups g = inst.Model.AnimationGroups;
            int count = 1;
            if (g.Node.Count > 0) count = Math.Max(count, g.Node[0].FrameCount);
            if (g.Material.Count > 0) count = Math.Max(count, g.Material[0].FrameCount);
            if (g.Texcoord.Count > 0) count = Math.Max(count, g.Texcoord[0].FrameCount);
            if (g.Texture.Count > 0) count = Math.Max(count, g.Texture[0].FrameCount);
            return count;
        }

        private static int GroupFrame(int frame, int groupFrames, bool loop)
        {
            if (groupFrames <= 1) return 0;
            return loop ? frame % groupFrames : Math.Min(frame, groupFrames - 1);
        }

        // Append the widget's triangles at `frame` (looping or holding the last frame) to `output`, alpha multiplied by
        // `alpha`.
        public void Evaluate(int widgetIndex, int frame, bool loop, float alpha, List<WidgetTri> output) =>
            Evaluate(Instance(widgetIndex), frame, loop, alpha, output);

        public void Evaluate(ModelInstance inst, int frame, bool loop, float alpha, List<WidgetTri> output)
        {
            Model model = inst.Model;
            AnimationGroups groups = model.AnimationGroups;
            AnimationInfo info = inst.AnimInfo;
            info.Node.Slot = info.Material.Slot = info.Texture.Slot = info.Texcoord.Slot = 0;
            info.Node.Group = groups.Node.Count > 0 ? groups.Node[0] : null;
            info.Material.Group = groups.Material.Count > 0 ? groups.Material[0] : null;
            info.Texture.Group = groups.Texture.Count > 0 ? groups.Texture[0] : null;
            info.Texcoord.Group = groups.Texcoord.Count > 0 ? groups.Texcoord[0] : null;

            info.Frame[0] = GroupFrame(frame, info.Material.Group?.FrameCount ?? 0, loop);
            model.AnimateMaterials(info);
            info.Frame[0] = GroupFrame(frame, info.Texture.Group?.FrameCount ?? 0, loop);
            model.AnimateTextures(info);
            model.ComputeNodeMatrices(index: 0);
            info.Frame[0] = GroupFrame(frame, info.Node.Group?.FrameCount ?? 0, loop);
            model.AnimateNodes(index: 0, useNodeTransform: true, Matrix4.Identity, model.Scale, info);
            model.UpdateMatrixStack();
            int texcoordFrame = GroupFrame(frame, info.Texcoord.Group?.FrameCount ?? 0, loop);

            if (!_meshes.TryGetValue(inst, out List<float[]>? meshes))
            {
                meshes = new List<float[]>();
                foreach (Mesh mesh in model.Meshes)
                {
                    meshes.Add(DsDisplayList.Decode(model, mesh, isRoom: false));
                }
                _meshes[inst] = meshes;
            }
            EmitNode(model, info, texcoordFrame, model.Nodes.Count > 0 ? 0 : -1, meshes, alpha, output);
        }

        private void EmitNode(Model model, AnimationInfo info, int texcoordFrame, int index, List<float[]> meshes,
            float alpha, List<WidgetTri> output)
        {
            for (int i = index; i != -1;)
            {
                Node node = model.Nodes[i];
                if (node.Enabled)
                {
                    int start = node.MeshId / 2;
                    for (int k = 0; k < node.MeshCount; k++)
                    {
                        EmitMesh(model, info, texcoordFrame, model.Meshes[start + k], meshes[start + k], node, alpha, output);
                    }
                    if (node.ChildIndex != -1)
                    {
                        EmitNode(model, info, texcoordFrame, node.ChildIndex, meshes, alpha, output);
                    }
                }
                i = node.NextIndex;
            }
        }

        private void EmitMesh(Model model, AnimationInfo info, int texcoordFrame, Mesh mesh, float[] verts, Node node,
            float alpha, List<WidgetTri> output)
        {
            if (!mesh.Visible) return;
            Material material = model.Materials[mesh.MaterialId];
            float matAlpha = material.CurrentAlpha * alpha;
            if (matAlpha <= 0) return;
            int textureId = -1;
            if (material.CurrentTextureId != -1)
            {
                textureId = TextureFor(model, material.CurrentTextureId, material.CurrentPaletteId);
            }
            Matrix4 texMatrix = Matrix4.Identity;
            if (info.Texcoord.Group != null && info.Texcoord.Group.Animations.TryGetValue(material.Name, out TexcoordAnimation anim))
            {
                texMatrix = model.AnimateTexcoords(info.Texcoord.Group, anim, texcoordFrame);
            }
            Vector3 diffuse = material.CurrentDiffuse;
            float scale = model.Scale.X;
            int stride = DsDisplayList.Stride;
            int count = verts.Length / stride;
            var tri = new WidgetTri
            {
                TextureId = textureId,
                WrapS = (UiWrap)material.XRepeat,
                WrapT = (UiWrap)material.YRepeat
            };
            float zSum = 0;
            for (int v = 0; v < count; v++)
            {
                int o = v * stride;
                int stack = (int)verts[o + 12];
                Matrix4 m = model.NodeMatrixIds.Count > 0 ? StackMatrix(model, stack) : node.Animation;
                Vector4 p = new Vector4(verts[o], verts[o + 1], verts[o + 2], 1) * m;
                Vector4 uv = new Vector4(verts[o + 10], verts[o + 11], 0, 1) * texMatrix;
                float ca = verts[o + 9];
                // colour: a COLOR command's colour, or the material diffuse when the list set none (MphRead passes
                // the diffuse as the current GL colour); DIF_AMB smuggles the diffuse in the colour too
                float r = ca == DsDisplayList.MaterialColor ? diffuse.X : verts[o + 6];
                float g = ca == DsDisplayList.MaterialColor ? diffuse.Y : verts[o + 7];
                float b = ca == DsDisplayList.MaterialColor ? diffuse.Z : verts[o + 8];
                var vertex = new UiVertex(p.X * scale, p.Y * scale, uv.X, uv.Y, r, g, b, matAlpha);
                zSum += p.Z * scale;
                switch (v % 3)
                {
                case 0: tri.A = vertex; break;
                case 1: tri.B = vertex; break;
                default:
                    tri.C = vertex;
                    tri.Z = zSum / 3;
                    zSum = 0;
                    output.Add(tri);
                    break;
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

        private int TextureFor(Model model, int textureId, int paletteId)
        {
            string key = $"{model.Name}|{textureId}|{paletteId}";
            return _textures.GetOrAdd(key, () =>
            {
                Texture texture = model.Recolors[0].Textures[textureId];
                IReadOnlyList<ColorRgba> pixels = model.GetPixels(textureId, paletteId, recolorId: 0);
                byte[] rgba = new byte[texture.Width * texture.Height * 4];
                for (int i = 0; i < pixels.Count && i * 4 + 3 < rgba.Length; i++)
                {
                    ColorRgba c = pixels[i];
                    rgba[i * 4] = c.Red;
                    rgba[i * 4 + 1] = c.Green;
                    rgba[i * 4 + 2] = c.Blue;
                    rgba[i * 4 + 3] = c.Alpha;
                }
                return (texture.Width, texture.Height, rgba);
            }).Id;
        }
    }
}
