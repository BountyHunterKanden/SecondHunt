using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats;

namespace MphRecomp.Arenas.Editor
{
    // MAP MAKER E1 (docs/MAP_EDITOR.md section 4): the block textures, picked from the textures the player's own import
    // holds (the multiplayer rooms' models). Nothing ships: each entry names a room and a texture/palette pair, and the
    // pixels are read at run time. The editor draws the RGBA pixels; the compiler copies the DS texture and palette into
    // the map's room model. A map stores only the ids below, so the list only ever grows.
    public sealed class TextureLibrary
    {
        public const int AutoFloor = 2, AutoWall = 7, AutoCeiling = 1;
        public const float TexelsPerUnit = 16;   // 128 texels cover 8 world units

        public sealed class Entry
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public string Room { get; init; } = "";
            public int TextureId { get; init; }
            public int PaletteId { get; init; }
            // the picture slides this many whole texture widths / heights per loop (240 animation steps, 8 s)
            public int ScrollS { get; init; }
            public int ScrollT { get; init; }
            public int Width { get; set; }
            public int Height { get; set; }
            public byte[] Rgba { get; set; } = Array.Empty<byte>();
            public Texture Texture { get; set; }
            public IReadOnlyList<TextureData> Data { get; set; } = Array.Empty<TextureData>();
            public IReadOnlyList<PaletteData>? Palette { get; set; }

            public bool Scrolls => ScrollS != 0 || ScrollT != 0;
            public bool Translucent { get; set; }
        }

        // id, name, room, texture, palette, scroll s, scroll t
        static readonly (int, string, string, int, int, int, int)[] Defaults =
        {
            (1, "Floor plate", "MP7 PROCESSOR CORE", 5, 5, 0, 0),
            (2, "Floor tile", "MP7 PROCESSOR CORE", 9, 9, 0, 0),
            (3, "Grating", "MP7 PROCESSOR CORE", 15, 15, 0, 0),
            (4, "Rock", "MP7 PROCESSOR CORE", 3, 3, 0, 0),
            (5, "Rust plate", "MP7 PROCESSOR CORE", 6, 6, 0, 0),
            (6, "Trim stripe", "MP7 PROCESSOR CORE", 25, 25, 0, 0),
            (7, "Wall panel", "MP1 SANCTORUS", 26, 26, 0, 0),
            (8, "Lava", "MP7 PROCESSOR CORE", 4, 4, 1, 0),
            (9, "Gold water", "MP3 PROVING GROUND", 5, 5, 0, 1),
            (10, "Ice", "MP9 CRYOCHASM", 5, 5, 0, 0),
            (11, "Snow", "MP9 CRYOCHASM", 12, 12, 0, 0),
            (12, "Glow strip", "MP3 PROVING GROUND", 14, 15, 0, 0),
            (13, "Amber glow", "MP3 PROVING GROUND", 21, 21, 0, 0),
            (14, "Sand stone", "MP3 PROVING GROUND", 19, 19, 0, 0),
        };

        public IReadOnlyList<Entry> Entries { get; }

        TextureLibrary(IReadOnlyList<Entry> entries) => Entries = entries;

        public Entry? Get(int id) => id >= 1 && id <= Entries.Count ? Entries[id - 1] : null;

        // null when the import has no multiplayer rooms (the editor then draws the flat colours)
        public static TextureLibrary? Load()
        {
            var entries = new List<Entry>();
            var models = new Dictionary<string, Model>();
            foreach ((int id, string name, string room, int tex, int pal, int ss, int st) in Defaults)
            {
                try
                {
                    if (!models.TryGetValue(room, out Model? model))
                    {
                        model = Read.GetRoomModelInstance(room).Model;
                        models[room] = model;
                    }
                    Recolor recolor = model.Recolors[0];
                    Texture t = recolor.Textures[tex];
                    IReadOnlyList<ColorRgba> px = model.GetPixels(tex, pal, 0);
                    var rgba = new byte[px.Count * 4];
                    bool translucent = false;
                    for (int i = 0; i < px.Count; i++)
                    {
                        rgba[i * 4] = px[i].Red; rgba[i * 4 + 1] = px[i].Green; rgba[i * 4 + 2] = px[i].Blue; rgba[i * 4 + 3] = px[i].Alpha;
                        if (px[i].Alpha < 255) translucent = true;
                    }
                    entries.Add(new Entry
                    {
                        Id = id, Name = name, Room = room, TextureId = tex, PaletteId = pal, ScrollS = ss, ScrollT = st,
                        Width = t.Width, Height = t.Height, Rgba = rgba, Texture = t,
                        Data = recolor.TextureData[tex],
                        Palette = pal >= 0 ? recolor.PaletteData[pal] : null,
                        Translucent = translucent
                    });
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Map maker textures: {room} texture {tex}: {e.Message}");
                    return null;
                }
            }
            return new TextureLibrary(entries);
        }

        // the auto texture of a face with this normal
        public static int AutoFor(OpenTK.Mathematics.Vector3 n) => n.Y > 0.7f ? AutoFloor : n.Y < -0.7f ? AutoCeiling : AutoWall;
    }
}
