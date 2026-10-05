using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MphRead;
using MphRead.Formats;

// Menu strings and the menu font, both from the user's ROM.
//   strings: frontend/metroidhunters_text_<lang>.bin (null-terminated list of entry offsets; each entry = offset,
//            offset, length, length -- the pairs always match), indexed by the menu file's string ids
//   font:    the glyph atlas in models/hudfont_Model.bin (128x256, 16 columns of 8x8 cells, glyph = char - 32) plus
//            the per-glyph advance widths and vertical offsets from arm9.bin that MphRead's HUD text also uses
namespace MphRecomp.Frontend
{
    public sealed class MenuStrings
    {
        private readonly List<string> _strings;
        public IReadOnlyList<string> Strings => _strings;
        public int RomCount { get; }

        private MenuStrings(List<string> strings)
        {
            _strings = strings;
            RomCount = strings.Count;
        }

        public string this[int id] => id >= 0 && id < _strings.Count ? _strings[id] : "";

        // the recomp's own text (labels, values that change): ids after the ROM's
        public int Add(string text)
        {
            _strings.Add(text);
            return _strings.Count - 1;
        }

        public void Set(int id, string text)
        {
            if (id >= RomCount && id < _strings.Count) _strings[id] = text;
        }

        // Fill one of the ROM's placeholder strings ("rumb", "priv", "setting a"...) the way the game's code does.
        public void Fill(int id, string text)
        {
            if (id >= 0 && id < RomCount) _strings[id] = text;
        }

        // lang: en, fr, de, it, es, jp
        public static MenuStrings Load(string fileSystemRoot, string lang = "en")
        {
            byte[] data = File.ReadAllBytes(Path.Combine(fileSystemRoot, "frontend", $"metroidhunters_text_{lang}.bin"));
            var list = new List<string>();
            for (int o = 0; ; o += 4)
            {
                uint entry = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o));
                if (entry == 0) break;
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)entry));
                ushort length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)entry + 8));
                int end = Array.IndexOf(data, (byte)0, (int)offset, length);
                // Latin-1 keeps each byte as one char, so the game's two-byte (0x80) sequences survive for the font
                list.Add(Encoding.Latin1.GetString(data, (int)offset, (end == -1 ? length : end - (int)offset)));
            }
            return new MenuStrings(list);
        }
    }

    public sealed class MenuFont
    {
        public const int MinCharacter = 32;
        public const int Cell = 8;
        private const int Columns = 16;

        private readonly IReadOnlyList<int> _widths;
        private readonly IReadOnlyList<int> _offsets;
        public int TextureId { get; }
        public int AtlasWidth { get; }
        public int AtlasHeight { get; }

        public MenuFont(UiTextureCache textures)
        {
            if ((object?)MphRead.Text.Font.Normal.Widths == null)
            {
                Extract.LoadRuntimeData(); // arm9.bin widths / offsets (MphRead's own loader)
            }
            _widths = MphRead.Text.Font.Normal.Widths;
            _offsets = MphRead.Text.Font.Normal.Offsets;
            Model model = Read.ReadModelFile("hudfont", "models/hudfont_Model.bin", null);
            Material material = model.Materials[0];
            Texture texture = model.Recolors[0].Textures[material.TextureId];
            AtlasWidth = texture.Width;
            AtlasHeight = texture.Height;
            TextureId = textures.GetOrAdd("hudfont", () =>
            {
                IReadOnlyList<ColorRgba> pixels = model.GetPixels(material.TextureId, material.PaletteId, recolorId: 0);
                byte[] rgba = new byte[texture.Width * texture.Height * 4];
                for (int i = 0; i < pixels.Count && i * 4 + 3 < rgba.Length; i++)
                {
                    rgba[i * 4] = pixels[i].Red;
                    rgba[i * 4 + 1] = pixels[i].Green;
                    rgba[i * 4 + 2] = pixels[i].Blue;
                    rgba[i * 4 + 3] = pixels[i].Alpha;
                }
                return (texture.Width, texture.Height, rgba);
            }).Id;
        }

        // the game's two-byte characters: a byte with the top bit set combines with the next one
        private static int Glyph(string text, ref int i)
        {
            int ch = text[i];
            if ((ch & 0x80) != 0 && i + 1 < text.Length)
            {
                ch = text[++i] & 0x3F | ((ch & 0x1F) << 6);
            }
            return ch - MinCharacter;
        }

        private int Advance(int glyph) => glyph >= 0 && glyph < _widths.Count ? _widths[glyph] : Cell;

        public float Measure(string line)
        {
            float width = 0;
            for (int i = 0; i < line.Length; i++)
            {
                width += Advance(Glyph(line, ref i));
            }
            return width;
        }

        // Break into lines at '\n', and at spaces when a line would pass wrapWidth (0 = never).
        public List<string> Lines(string text, int wrapWidth)
        {
            var lines = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                if (wrapWidth <= 0 || Measure(raw) <= wrapWidth)
                {
                    lines.Add(raw);
                    continue;
                }
                var sb = new StringBuilder();
                foreach (string word in raw.Split(' '))
                {
                    string candidate = sb.Length == 0 ? word : sb + " " + word;
                    if (sb.Length > 0 && Measure(candidate) > wrapWidth)
                    {
                        lines.Add(sb.ToString());
                        sb.Clear().Append(word);
                    }
                    else
                    {
                        sb.Clear().Append(candidate);
                    }
                }
                lines.Add(sb.ToString());
            }
            return lines;
        }

        // Emit text as glyph quads in MENU SPACE (Y up). (x, y) is the anchor in the menu file's text coordinates
        // (Y up from the bottom of the touch screen, so menu-space y = textY - 192): align 0 = left edge at x,
        // 1 = right edge at x, 2 = centred on x. Lines run downward `lineHeight` apart; y is the top of the first line.
        public void Emit(string text, float x, float y, int align, int wrapWidth, float lineHeight,
            float r, float g, float b, float a, float z, List<WidgetTri> output)
        {
            float top = y - 192 + lineHeight; // the anchor is the BOTTOM of the first line (checked against the game)
            foreach (string line in Lines(text, wrapWidth))
            {
                float width = Measure(line);
                float penX = align == 1 ? x - width : align == 2 ? x - MathF.Floor(width / 2) : x;
                for (int i = 0; i < line.Length; i++)
                {
                    bool space = line[i] == ' ';
                    int glyph = Glyph(line, ref i);
                    if (!space && glyph >= 0 && glyph < _widths.Count)
                    {
                        float gy = top - (glyph < _offsets.Count ? _offsets[glyph] : 0);
                        // inset a hair so scaled, nearest-sampled glyphs never pick up the neighbouring cell
                        const float Inset = 0.02f;
                        float u0 = ((glyph % Columns) * Cell + Inset) / AtlasWidth;
                        float v0 = ((glyph / Columns) * Cell + Inset) / AtlasHeight;
                        float u1 = u0 + (Cell - 2 * Inset) / AtlasWidth;
                        float v1 = v0 + (Cell - 2 * Inset) / AtlasHeight;
                        var p0 = new UiVertex(penX, gy, u0, v0, r, g, b, a);
                        var p1 = new UiVertex(penX + Cell, gy, u1, v0, r, g, b, a);
                        var p2 = new UiVertex(penX + Cell, gy - Cell, u1, v1, r, g, b, a);
                        var p3 = new UiVertex(penX, gy - Cell, u0, v1, r, g, b, a);
                        output.Add(new WidgetTri { A = p0, B = p1, C = p2, Z = z, TextureId = TextureId });
                        output.Add(new WidgetTri { A = p0, B = p2, C = p3, Z = z, TextureId = TextureId });
                    }
                    penX += Advance(glyph);
                }
                top -= lineHeight;
            }
        }
    }
}
