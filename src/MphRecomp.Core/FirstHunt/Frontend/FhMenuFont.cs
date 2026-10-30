using System.Collections.Generic;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

namespace MphRecomp.FirstHunt
{
    // a text item's text (FhMenuItem: label or the page's fill), its style word (+0x68) and advance (+0x6c)
    public readonly record struct FhText(string Text, uint Style, int Advance);

    // First Hunt's menu text (fh_research p4_frontend_draw.md section 9; per-item setup 0x2045c5c, glyph emitter
    // 0x204607c), from menusTD/font_Model.bin's three atlases of 32 x 4 cells, chars 0x21-0xa0:
    //   style 0x1000: b_16x16, 16 px cells, raised 8 px; 0x800: c_6x6 (8 px cells); otherwise a_8x8
    //   style 0x4000: centred on the anchor, 0x2000: right-aligned, otherwise left; advance = +0x6c px a char (x2 big)
    // A glyph's top left is at the anchor, the text going right and its cells down; its colour and alpha are the text
    // node's material at the node's frame (so the page's _hi / _dehi states fade and recolour it).
    public sealed class FhMenuFont
    {
        public const uint Big = 0x1000, Small = 0x800, Centre = 0x4000, Right = 0x2000;

        private readonly FhMenuModel _model;

        public FhMenuFont()
        {
            _model = new FhMenuModel("font", animated: false);
        }

        public void Emit(UiTextureCache textures, List<WidgetTri> output, FhText text, Matrix4 node, float modelScale,
            Vector3 colour, float alpha, Vector2 offset)
        {
            bool big = (text.Style & Big) != 0;
            int material = big ? 1 : (text.Style & Small) != 0 ? 2 : 0;
            (int id, int width, int height) = _model.MaterialTexture(textures, material);
            int cell = big ? 16 : 8;
            float advance = text.Advance * (big ? 2 : 1);
            float x = 0;
            float y = big ? 8 : 0;
            float span = text.Text.Length * advance;
            if ((text.Style & Centre) != 0) x -= span / 2;
            else if ((text.Style & Right) != 0) x -= span;
            float z = node.Row3.Z * modelScale;
            foreach (char c in text.Text)
            {
                int g = (sbyte)(byte)c - 0x21;
                if (c != ' ' && g >= 0)
                {
                    int col = g & 31, row = g >> 5;
                    float u0 = col * cell / (float)width, v0 = row * cell / (float)height;
                    float u1 = (col + 1) * cell / (float)width, v1 = (row + 1) * cell / (float)height;
                    UiVertex V(float px, float py, float u, float v)
                    {
                        Vector4 p = new Vector4(px / modelScale, py / modelScale, 0, 1) * node;
                        return new UiVertex(p.X * modelScale + offset.X, p.Y * modelScale + offset.Y, u, v, colour.X, colour.Y, colour.Z, alpha);
                    }
                    UiVertex a = V(x, y, u0, v0), b = V(x, y - cell, u0, v1), cc = V(x + cell, y - cell, u1, v1), d = V(x + cell, y, u1, v0);
                    output.Add(new WidgetTri { TextureId = id, A = a, B = b, C = cc, Z = z });
                    output.Add(new WidgetTri { TextureId = id, A = a, B = cc, C = d, Z = z });
                }
                x += advance;
            }
        }
    }
}
