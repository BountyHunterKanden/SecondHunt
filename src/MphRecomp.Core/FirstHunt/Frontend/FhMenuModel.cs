using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRead.Formats;
using MphRecomp.Frontend;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.FirstHunt
{
    // A First Hunt menu model (menusTD/<name>_Model.bin + _Anim.bin) as the cart draws it (fh_research p4_frontend.md):
    // its units are DS pixels in the menu space MenuLayout uses (top screen y 0..192, touch screen y -192..0). Each node
    // can be bound to a menu_state entry (0x20462f0, by name) and is drawn at that entry's current frame of the model's one
    // animation timeline (0x20462cc); a node with no binding draws at frame 0 (the frame model: its own loop, DefaultFrame).
    // A state steps its entries every other 60 Hz tick (30 fps) while any node is bound to it (0x20479d4 -> 0x2049ff4).
    // The page draw (0x2046868) walks the nodes in index order and draws only the direct children of topScreen and
    // bottomScreen, each on its own; a text item's node draws its text there instead (fh_research p4_frontend_draw.md).
    public sealed class FhMenuModel
    {
        public string Name { get; }
        public Model Model { get; }

        private sealed class PlayingState
        {
            public FhMenuState State = null!;
            public int Tick;
            public short[] Cur = Array.Empty<short>();
            public int Stamp = -1;
            public bool Finished => Tick / 2 > State.Length;
        }

        private readonly (PlayingState Playing, int Entry)?[] _bound;
        private readonly List<float[]> _meshes = new();
        private int _stamp = 0;
        // a texture animation's keys number its own frames (the file's list, not the model's textures): a material's
        // lowest key is its own texture/palette and the rest follow on in the model (frame: LightningL keys 0-7 =
        // textures 12-19, lightning_small1 8-11 = 20-23; host_start Stage1 0-1 = textures 27-28, palettes 20-21);
        // per material: the offsets added to its keys
        private readonly Dictionary<string, (int Texture, int Palette)> _textureAnimBase = new();
        public IReadOnlyDictionary<string, (int Texture, int Palette)> TextureAnimOffsets => _textureAnimBase;
        private readonly int _topRoot, _bottomRoot;

        // the frame a node with no binding draws at
        public int DefaultFrame { get; set; }

        // a backdrop (<page>_bg, 0x2046524): only the meshes showing textures i and i + 1, the image pair the cart copies
        // into its BG layer (configuration_bg has five, picked by control type); -1 = every mesh
        public int ImagePair { get; set; } = -1;

        // a backdrop: the cart copies its textures into a 2D BG layer (0x20504b8), so they show as they are, not modulated
        // by the model's vertex or material colours
        public bool Flat { get; set; }

        public FhMenuModel(string name, bool animated = true)
        {
            Name = name;
            string anim = $"menusTD/{name}_Anim.bin";
            bool hasAnim = animated && File.Exists(Paths.Combine(Paths.FhFileSystem, anim));
            Model = Read.ReadModelFile("fhmenu:" + name, $"menusTD/{name}_Model.bin", hasAnim ? anim : null, firstHunt: true);
            _bound = new (PlayingState, int)?[Model.Nodes.Count];
            foreach (Mesh mesh in Model.Meshes)
            {
                _meshes.Add(DsDisplayList.Decode(Model, mesh, isRoom: false));
            }
            Model.ComputeNodeMatrices(index: 0);
            TextureAnimBases();
            _topRoot = NodeIndex("topScreen");
            _bottomRoot = NodeIndex("bottomScreen");
        }

        public int FrameCount
        {
            get
            {
                AnimationGroups g = Model.AnimationGroups;
                return g.Node.Count > 0 ? g.Node[0].FrameCount : g.Material.Count > 0 ? g.Material[0].FrameCount
                    : g.Texture.Count > 0 ? g.Texture[0].FrameCount : 1;
            }
        }

        public int NodeIndex(string node)
        {
            for (int i = 0; i < Model.Nodes.Count; i++)
            {
                if (Model.Nodes[i].Name == node) return i;
            }
            return -1;
        }

        // 0x204620c / 0x20462f0: start a state; the nodes it names are bound to it from its start frames, the others keep
        // theirs (or lose them with clear). Returns false if the state doesn't exist.
        public bool Play(FhMenuState? state, bool clear = false)
        {
            if (state == null) return false;
            var playing = new PlayingState { State = state, Cur = new short[state.Nodes.Count] };
            for (int e = 0; e < state.Nodes.Count; e++)
            {
                playing.Cur[e] = state.Nodes[e].Start;
            }
            for (int i = 0; i < Model.Nodes.Count; i++)
            {
                string name = Model.Nodes[i].Name;
                int entry = -1;
                for (int e = 0; e < state.Nodes.Count; e++)
                {
                    if (state.Nodes[e].Node == name)
                    {
                        entry = e;
                        break;
                    }
                }
                if (entry >= 0) _bound[i] = (playing, entry);
                else if (clear) _bound[i] = null;
            }
            return true;
        }

        // the state last started has run its length (and any other bound one)
        public bool Finished(FhMenuState state)
        {
            foreach (var b in _bound)
            {
                if (b is { } bound && bound.Playing.State == state && !bound.Playing.Finished) return false;
            }
            return true;
        }

        // one 60 Hz tick: each state with a bound node steps once (0x2049ff4: every other tick, each entry moves one
        // frame toward its end, then jumps to its loop frame)
        public void Tick()
        {
            _stamp++;
            foreach (var b in _bound)
            {
                if (b is not { } bound || bound.Playing.Stamp == _stamp) continue;
                PlayingState p = bound.Playing;
                p.Stamp = _stamp;
                p.Tick++;
                if (p.Tick % 2 != 0) continue;
                for (int e = 0; e < p.State.Nodes.Count; e++)
                {
                    FhMenuStateNode n = p.State.Nodes[e];
                    if (n.End > n.Start)
                    {
                        p.Cur[e]++;
                        if (p.Cur[e] > n.End) p.Cur[e] = n.Loop;
                    }
                    else if (n.End < n.Start)
                    {
                        p.Cur[e]--;
                        if (p.Cur[e] < n.End) p.Cur[e] = n.Loop;
                    }
                }
            }
        }

        // the frame each node draws at: its binding's, else DefaultFrame
        private int[] NodeFrames()
        {
            int[] frames = new int[Model.Nodes.Count];
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i] = _bound[i] is { } b ? b.Playing.Cur[b.Entry] : DefaultFrame;
            }
            return frames;
        }

        public int FrameOf(string node)
        {
            int i = NodeIndex(node);
            return i < 0 ? -1 : NodeFrames()[i];
        }

        // the model's triangles in menu space at the nodes' frames; hidden: node names not drawn (the page's own
        // switches, e.g. a disabled button); texts: the text items by node, drawn with font in their node's place;
        // top / bottom: which screen's nodes
        public void Evaluate(UiTextureCache textures, List<WidgetTri> output, float alpha = 1, ISet<string>? hidden = null,
            Vector2 offset = default, IReadOnlyDictionary<string, FhText>? texts = null, FhMenuFont? font = null,
            bool top = true, bool bottom = true)
        {
            int[] frames = NodeFrames();
            AnimationGroups groups = Model.AnimationGroups;
            NodeAnimationGroup? nodeGroup = groups.Node.Count > 0 ? groups.Node[0] : null;
            MaterialAnimationGroup? materialGroup = groups.Material.Count > 0 ? groups.Material[0] : null;
            TextureAnimationGroup? textureGroup = groups.Texture.Count > 0 ? groups.Texture[0] : null;
            TexcoordAnimationGroup? texcoordGroup = groups.Texcoord.Count > 0 ? groups.Texcoord[0] : null;
            // node matrices in tree order (a parent before its children)
            for (int i = 0; i < Model.Nodes.Count; i++)
            {
                Node node = Model.Nodes[i];
                Matrix4 transform = node.Transform;
                if (nodeGroup != null && nodeGroup.Animations.TryGetValue(node.Name, out NodeAnimation animation))
                {
                    transform = AnimateNode(nodeGroup, animation, Clamp(frames[i], nodeGroup.FrameCount));
                    if (node.ParentIndex != -1 && !node.AnimIgnoreParent)
                    {
                        transform *= Model.Nodes[node.ParentIndex].Animation;
                    }
                }
                node.Animation = transform;
            }
            Model.UpdateMatrixStack();
            for (int i = 0; i < Model.Nodes.Count; i++)
            {
                Node node = Model.Nodes[i];
                if (!node.Enabled || hidden != null && hidden.Contains(node.Name)) continue;
                if (_topRoot >= 0 || _bottomRoot >= 0)
                {
                    bool onTop = top && _topRoot >= 0 && node.ParentIndex == _topRoot;
                    bool onBottom = bottom && _bottomRoot >= 0 && node.ParentIndex == _bottomRoot;
                    if (!onTop && !onBottom) continue;
                }
                else if (!AncestorsEnabled(i, hidden)) continue;
                // materials at the frame of the node drawing them (nodes share materials: the text lines' TextInfo)
                for (int k = 0; k < node.MeshCount; k++)
                {
                    AnimateMaterial(Model.Materials[Model.Meshes[node.MeshId / 2 + k].MaterialId], materialGroup, textureGroup, frames[i]);
                }
                int start = output.Count;
                if (texts != null && texts.TryGetValue(node.Name, out FhText text))
                {
                    if (font != null && node.MeshCount > 0 && text.Text.Length > 0)
                    {
                        Material material = Model.Materials[Model.Meshes[node.MeshId / 2].MaterialId];
                        float a = material.CurrentAlpha * alpha;
                        if (a > 0)
                        {
                            font.Emit(textures, output, text, node.Animation, Model.Scale.X, material.CurrentDiffuse, a, offset);
                        }
                    }
                    TagNode(output, start, i);
                    continue;
                }
                for (int k = 0; k < node.MeshCount; k++)
                {
                    int meshId = node.MeshId / 2 + k;
                    Mesh mesh = Model.Meshes[meshId];
                    Material material = Model.Materials[mesh.MaterialId];
                    if (ImagePair >= 0 && material.TextureId != ImagePair && material.TextureId != ImagePair + 1) continue;
                    Matrix4 texMatrix = TexcoordMatrix(material, texcoordGroup, frames[i]);
                    EmitMesh(textures, mesh, _meshes[meshId], node, texMatrix, alpha, offset, output);
                }
                TagNode(output, start, i);
            }
        }

        // which model and node drew a triangle (WidgetTri.Item): Tag << 12 | node + 1, for layouts that move nodes
        public int Tag { get; set; }

        public static int ItemOf(int tag, int node) => tag << 12 | node + 1;

        private void TagNode(List<WidgetTri> output, int start, int node)
        {
            int item = ItemOf(Tag, node);
            for (int k = start; k < output.Count; k++)
            {
                WidgetTri t = output[k];
                t.Item = item;
                output[k] = t;
            }
        }

        // one node's meshes again at a place in menu space (the frame's touch trail, 0x2049a80: the node alone, no model
        // animation, so the material's own texcoord scale and no texcoord animation), its material alpha replaced
        public void EvaluateNodeAt(UiTextureCache textures, List<WidgetTri> output, string name, Vector3 at, float alpha)
        {
            int i = NodeIndex(name);
            if (i < 0) return;
            Node node = Model.Nodes[i];
            Matrix4 saved = node.Animation;
            node.Animation = Matrix4.CreateTranslation(at.X / Model.Scale.X, at.Y / Model.Scale.Y, at.Z / Model.Scale.Z);
            for (int k = 0; k < node.MeshCount; k++)
            {
                int meshId = node.MeshId / 2 + k;
                Mesh mesh = Model.Meshes[meshId];
                Material material = Model.Materials[mesh.MaterialId];
                float savedAlpha = material.CurrentAlpha;
                material.CurrentAlpha = alpha;
                EmitMesh(textures, mesh, _meshes[meshId], node, TexcoordMatrix(material, null, 0), 1, default, output, forceNode: true);
                material.CurrentAlpha = savedAlpha;
            }
            node.Animation = saved;
        }

        // one material as a quad the game's code places itself (the lit button's sparks, 0x2049728): corners in menu space,
        // texcoords in texels, at the model's default frame, its alpha replaced; z: depth among the other triangles
        public void EmitMaterialQuad(UiTextureCache textures, List<WidgetTri> output, string materialName, Vector2[] at,
            Vector2[] st, float alpha, float z)
        {
            Material? material = Model.Materials.FirstOrDefault(m => m.Name == materialName);
            if (material == null || alpha <= 0) return;
            AnimationGroups groups = Model.AnimationGroups;
            AnimateMaterial(material, groups.Material.Count > 0 ? groups.Material[0] : null,
                groups.Texture.Count > 0 ? groups.Texture[0] : null, DefaultFrame);
            if (material.CurrentTextureId < 0) return;
            Texture texture = Model.Recolors[0].Textures[material.CurrentTextureId];
            int textureId = TextureFor(textures, material.CurrentTextureId, material.CurrentPaletteId);
            Vector3 c = material.CurrentDiffuse;
            UiVertex V(int i) => new UiVertex(at[i].X, at[i].Y, st[i].X / texture.Width, st[i].Y / texture.Height, c.X, c.Y, c.Z, alpha);
            var tri = new WidgetTri { TextureId = textureId, WrapS = (UiWrap)material.XRepeat, WrapT = (UiWrap)material.YRepeat, Z = z };
            tri.A = V(0); tri.B = V(1); tri.C = V(2);
            output.Add(tri);
            tri.A = V(0); tri.B = V(2); tri.C = V(3);
            output.Add(tri);
        }

        // a material's texture in the cache (the font's atlases): id and size
        public (int Id, int Width, int Height) MaterialTexture(UiTextureCache textures, int materialId)
        {
            Material material = Model.Materials[materialId];
            Texture texture = Model.Recolors[0].Textures[material.TextureId];
            return (TextureFor(textures, material.TextureId, material.PaletteId), texture.Width, texture.Height);
        }

        private bool AncestorsEnabled(int index, ISet<string>? hidden)
        {
            for (int p = Model.Nodes[index].ParentIndex; p != -1; p = Model.Nodes[p].ParentIndex)
            {
                if (!Model.Nodes[p].Enabled || hidden != null && hidden.Contains(Model.Nodes[p].Name)) return false;
            }
            return true;
        }

        private static int Clamp(int frame, int count) => count <= 1 ? 0 : Math.Clamp(frame, 0, count - 1);

        // Model.AnimateNode for one node at its own frame
        private Matrix4 AnimateNode(NodeAnimationGroup group, NodeAnimation a, int f)
        {
            int n = group.FrameCount;
            Vector3 s = Model.Scale;
            float sx = Model.InterpolateAnimation(group.Scales, a.ScaleLutIndexX, f, a.ScaleBlendX, a.ScaleLutLengthX, n);
            float sy = Model.InterpolateAnimation(group.Scales, a.ScaleLutIndexY, f, a.ScaleBlendY, a.ScaleLutLengthY, n);
            float sz = Model.InterpolateAnimation(group.Scales, a.ScaleLutIndexZ, f, a.ScaleBlendZ, a.ScaleLutLengthZ, n);
            float rx = Model.InterpolateAnimation(group.Rotations, a.RotateLutIndexX, f, a.RotateBlendX, a.RotateLutLengthX, n, isRotation: true);
            float ry = Model.InterpolateAnimation(group.Rotations, a.RotateLutIndexY, f, a.RotateBlendY, a.RotateLutLengthY, n, isRotation: true);
            float rz = Model.InterpolateAnimation(group.Rotations, a.RotateLutIndexZ, f, a.RotateBlendZ, a.RotateLutLengthZ, n, isRotation: true);
            float tx = Model.InterpolateAnimation(group.Translations, a.TranslateLutIndexX, f, a.TranslateBlendX, a.TranslateLutLengthX, n);
            float ty = Model.InterpolateAnimation(group.Translations, a.TranslateLutIndexY, f, a.TranslateBlendY, a.TranslateLutLengthY, n);
            float tz = Model.InterpolateAnimation(group.Translations, a.TranslateLutIndexZ, f, a.TranslateBlendZ, a.TranslateLutLengthZ, n);
            Matrix4 m = Matrix4.CreateTranslation(tx / s.X, ty / s.Y, tz / s.Z);
            m = DetMatrix.CreateRotationX(rx) * DetMatrix.CreateRotationY(ry) * DetMatrix.CreateRotationZ(rz) * m;
            return Matrix4.CreateScale(sx, sy, sz) * m;
        }

        // Model.AnimateMaterials + AnimateTextures for one material at its own frame
        // a material's texcoord transform, as MphRead's entity draw (EntityBase, First Hunt): none unless the material has
        // a texgen mode; then its texcoord animation at the frame if it has one, else its own scale / translate / rotate
        // (e.g. the training page's hex brackets: a 32x32 corner on a 32x64 quad, t scaled 2 and mirrored)
        private Matrix4 TexcoordMatrix(Material material, TexcoordAnimationGroup? group, int frame)
        {
            if (material.TexgenMode == TexgenMode.None) return Matrix4.Identity;
            if (group != null && group.Animations.TryGetValue(material.Name, out TexcoordAnimation tc))
            {
                // an animation's values go into the same SRT as the material's own (MphRead's AnimateTexcoords leaves
                // the translation unscaled: configuration's five labels, scale 0.156 and translate 0..-4 over one
                // atlas, would all wrap round to the first)
                int f = Clamp(frame, group.FrameCount), n = group.FrameCount;
                return Srt(Model.InterpolateAnimation(group.Scales, tc.ScaleLutIndexS, f, tc.ScaleBlendS, tc.ScaleLutLengthS, n),
                    Model.InterpolateAnimation(group.Scales, tc.ScaleLutIndexT, f, tc.ScaleBlendT, tc.ScaleLutLengthT, n),
                    Model.InterpolateAnimation(group.Rotations, tc.RotateLutIndexZ, f, tc.RotateBlendZ, tc.RotateLutLengthZ, n, isRotation: true),
                    Model.InterpolateAnimation(group.Translations, tc.TranslateLutIndexS, f, tc.TranslateBlendS, tc.TranslateLutLengthS, n),
                    Model.InterpolateAnimation(group.Translations, tc.TranslateLutIndexT, f, tc.TranslateBlendT, tc.TranslateLutLengthT, n));
            }
            return Srt(material.ScaleS, material.ScaleT, material.RotateZ, material.TranslateS, material.TranslateT);
        }

        private static Matrix4 Srt(float scaleS, float scaleT, float rotateZ, float translateS, float translateT)
        {
            Matrix4 m = Matrix4.CreateTranslation(scaleS * translateS, scaleT * translateT, 0);
            m = Matrix4.CreateScale(scaleS, scaleT, 1) * m;
            if (rotateZ == 0) return m;
            // a rotation turns the texture about its centre (Maya texture SRT): the training page's right-hand hex
            // brackets are the left ones turned 180 degrees, U 1..0 (about the origin U would be -1..0, all clamped edge)
            Matrix4 rotate = Matrix4.CreateTranslation(-0.5f, -0.5f, 0) * DetMatrix.CreateRotationZ(rotateZ)
                * Matrix4.CreateTranslation(0.5f, 0.5f, 0);
            return rotate * m;
        }

        private void TextureAnimBases()
        {
            if (Model.AnimationGroups.Texture.Count == 0) return;
            TextureAnimationGroup group = Model.AnimationGroups.Texture[0];
            int textureCount = Model.Recolors[0].Textures.Count, paletteCount = Model.Recolors[0].Palettes.Count;
            foreach (Material material in Model.Materials)
            {
                if (material.TextureId < 0 || !group.Animations.TryGetValue(material.Name, out TextureAnimation t) || t.Count == 0) continue;
                int lowT = int.MaxValue, highT = 0, lowP = int.MaxValue, highP = 0;
                for (int j = t.StartIndex; j < t.StartIndex + t.Count; j++)
                {
                    lowT = Math.Min(lowT, group.TextureIds[j]);
                    highT = Math.Max(highT, group.TextureIds[j]);
                    lowP = Math.Min(lowP, group.PaletteIds[j]);
                    highP = Math.Max(highP, group.PaletteIds[j]);
                }
                int dt = material.TextureId - lowT, dp = material.PaletteId - lowP;
                if (highT + dt < textureCount && highP + dp < paletteCount && lowP + dp >= 0)
                {
                    _textureAnimBase[material.Name] = (dt, dp);
                }
            }
        }

        private void AnimateMaterial(Material material, MaterialAnimationGroup? group, TextureAnimationGroup? textures, int frame)
        {
            material.CurrentDiffuse = material.Diffuse / 31.0f;
            material.CurrentAmbient = material.Ambient / 31.0f;
            material.CurrentSpecular = material.Specular / 31.0f;
            material.CurrentAlpha = material.Alpha / 31.0f;
            if (group != null && group.Animations.TryGetValue(material.Name, out MaterialAnimation a))
            {
                int f = Clamp(frame, group.FrameCount);
                int n = group.FrameCount;
                if (!material.AnimationFlags.TestFlag(MatAnimFlags.DisableColor))
                {
                    material.CurrentDiffuse = new Vector3(
                        Model.InterpolateAnimation(group.Colors, a.DiffuseLutIndexR, f, a.DiffuseBlendR, a.DiffuseLutLengthR, n),
                        Model.InterpolateAnimation(group.Colors, a.DiffuseLutIndexG, f, a.DiffuseBlendG, a.DiffuseLutLengthG, n),
                        Model.InterpolateAnimation(group.Colors, a.DiffuseLutIndexB, f, a.DiffuseBlendB, a.DiffuseLutLengthB, n)) / 31.0f;
                }
                if (!material.AnimationFlags.TestFlag(MatAnimFlags.DisableAlpha))
                {
                    material.CurrentAlpha = Model.InterpolateAnimation(group.Colors, a.AlphaLutIndex, f, a.AlphaBlend, a.AlphaLutLength, n) / 31.0f;
                }
            }
            material.CurrentTextureId = material.TextureId;
            material.CurrentPaletteId = material.PaletteId;
            if (textures != null && textures.Animations.TryGetValue(material.Name, out TextureAnimation t))
            {
                int f = Clamp(frame, textures.FrameCount);
                _textureAnimBase.TryGetValue(material.Name, out (int Texture, int Palette) add);
                // the last key at or before the frame
                for (int j = t.StartIndex; j < t.StartIndex + t.Count; j++)
                {
                    if (textures.FrameIndices[j] <= f)
                    {
                        material.CurrentTextureId = textures.TextureIds[j] + add.Texture;
                        material.CurrentPaletteId = textures.PaletteIds[j] + add.Palette;
                    }
                }
            }
        }

        private void EmitMesh(UiTextureCache textures, Mesh mesh, float[] verts, Node node, Matrix4 texMatrix, float alpha,
            Vector2 offset, List<WidgetTri> output, bool forceNode = false)
        {
            if (!mesh.Visible) return;
            Material material = Model.Materials[mesh.MaterialId];
            float matAlpha = material.CurrentAlpha * alpha;
            if (matAlpha <= 0) return;
            int textureId = material.CurrentTextureId == -1 ? -1 : TextureFor(textures, material.CurrentTextureId, material.CurrentPaletteId);
            Vector3 diffuse = material.CurrentDiffuse;
            bool lit = material.Lighting != 0;
            if (lit)
            {
                // a lit material comes out at about twice its diffuse on the cart, clamped (fh_ref: host_start's YOU card
                // YouBG 31,19,0 / YouBG1 31,26,17 show 31,31,0 / white, the frame bars 31,8,0 show 31,15,0)
                diffuse = Vector3.ComponentMin(diffuse * (2 * 31 / 32f), Vector3.One);
            }
            float scale = Model.Scale.X;
            int stride = DsDisplayList.Stride;
            int count = verts.Length / stride;
            var tri = new WidgetTri { TextureId = textureId, WrapS = (UiWrap)material.XRepeat, WrapT = (UiWrap)material.YRepeat };
            float zSum = 0;
            for (int v = 0; v < count; v++)
            {
                int o = v * stride;
                int stack = (int)verts[o + 12];
                Matrix4 m = Model.NodeMatrixIds.Count > 0 && !forceNode ? StackMatrix(stack) : node.Animation;
                Vector4 p = new Vector4(verts[o], verts[o + 1], verts[o + 2], 1) * m;
                Vector4 uv = new Vector4(verts[o + 10], verts[o + 11], 0, 1) * texMatrix;
                float ca = verts[o + 9];
                float r = Flat ? 1 : lit || ca == DsDisplayList.MaterialColor ? diffuse.X : verts[o + 6];
                float g = Flat ? 1 : lit || ca == DsDisplayList.MaterialColor ? diffuse.Y : verts[o + 7];
                float b = Flat ? 1 : lit || ca == DsDisplayList.MaterialColor ? diffuse.Z : verts[o + 8];
                var vertex = new UiVertex(p.X * scale + offset.X, p.Y * scale + offset.Y, uv.X, uv.Y, r, g, b, matAlpha);
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

        private Matrix4 StackMatrix(int index)
        {
            IReadOnlyList<float> s = Model.MatrixStackValues;
            int o = index * 16;
            return new Matrix4(s[o], s[o + 1], s[o + 2], s[o + 3], s[o + 4], s[o + 5], s[o + 6], s[o + 7],
                s[o + 8], s[o + 9], s[o + 10], s[o + 11], s[o + 12], s[o + 13], s[o + 14], s[o + 15]);
        }

        private int TextureFor(UiTextureCache textures, int textureId, int paletteId)
        {
            return textures.GetOrAdd($"fhmenu/{Name}|{textureId}|{paletteId}", () =>
            {
                Texture texture = Model.Recolors[0].Textures[textureId];
                IReadOnlyList<ColorRgba> pixels = Model.GetPixels(textureId, paletteId, recolorId: 0);
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
