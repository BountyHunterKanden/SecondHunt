using System;
using System.Collections.Generic;

// What the front end hands a platform to draw each frame: textured, vertex-coloured triangles in canvas pixels (origin
// top-left, Y down), grouped into batches that share a texture, wrap mode, blend mode and clip rectangle. Everything
// menu-specific (widget animation, text layout, the 16:9 recomposition) is resolved on the CPU before this point, so
// a platform only needs one trivial shader: texture * vertex colour, alpha blended. The PC tools rasterise the same list
// in software to check it.
namespace MphRecomp.Frontend
{
    public enum UiWrap : byte { Clamp = 0, Repeat = 1, Mirror = 2 }

    public enum UiBlend : byte { Alpha = 0, Additive = 1 }

    // An RGBA8 image the draw list refers to by Id. Pixels never change after creation (a new look = a new texture),
    // so a platform can upload each Id once.
    public sealed class UiTexture
    {
        public int Id { get; init; }
        public string Key { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] Rgba { get; init; } = Array.Empty<byte>();
    }

    // Interns textures by key ("<model path>|<texture>|<palette>", "font", ...). Shared by every screen.
    public sealed class UiTextureCache
    {
        private readonly Dictionary<string, UiTexture> _byKey = new();
        private readonly List<UiTexture> _byId = new();

        public int Count => _byId.Count;
        public UiTexture this[int id] => _byId[id];

        public UiTexture GetOrAdd(string key, Func<(int Width, int Height, byte[] Rgba)> create)
        {
            if (_byKey.TryGetValue(key, out UiTexture? tex)) return tex;
            (int w, int h, byte[] rgba) = create();
            tex = new UiTexture { Id = _byId.Count, Key = key, Width = w, Height = h, Rgba = rgba };
            _byId.Add(tex);
            _byKey[key] = tex;
            return tex;
        }

        public bool TryGet(string key, out UiTexture? tex) => _byKey.TryGetValue(key, out tex);
    }

    public struct UiVertex
    {
        public float X, Y, U, V, R, G, B, A;

        public UiVertex(float x, float y, float u, float v, float r, float g, float b, float a)
        {
            X = x; Y = y; U = u; V = v; R = r; G = g; B = b; A = a;
        }
    }

    public readonly record struct UiRect(float X, float Y, float W, float H)
    {
        public static readonly UiRect None = new(0, 0, 0, 0);
        public bool IsNone => W <= 0 || H <= 0;
    }

    public sealed class UiBatch
    {
        public int TextureId { get; init; } = -1; // -1 = untextured (vertex colour only)
        public UiWrap WrapS { get; init; }
        public UiWrap WrapT { get; init; }
        public UiBlend Blend { get; init; }
        public UiRect Clip { get; init; } = UiRect.None; // canvas pixels; None = no clipping
        public int Start { get; init; } // first vertex
        public int Count { get; set; } // vertex count (multiple of 3)
    }

    public sealed class UiDrawList
    {
        public List<UiVertex> Vertices { get; } = new();
        public List<UiBatch> Batches { get; } = new();
        public UiTextureCache Textures { get; }

        // the clip rectangle applied to triangles added from now on (set by the layout per region)
        public UiRect Clip { get; set; } = UiRect.None;

        // bumped by Clear: a platform can tell a list it already uploaded from a rebuilt one
        public int Version { get; private set; }

        public UiDrawList(UiTextureCache textures) => Textures = textures;

        public void Clear()
        {
            Version++;
            Vertices.Clear();
            Batches.Clear();
            Clip = UiRect.None;
        }

        public void Triangle(int textureId, UiWrap wrapS, UiWrap wrapT, UiBlend blend, in UiVertex a, in UiVertex b, in UiVertex c)
        {
            UiBatch? last = Batches.Count > 0 ? Batches[^1] : null;
            if (last == null || last.TextureId != textureId || last.WrapS != wrapS || last.WrapT != wrapT
                || last.Blend != blend || last.Clip != Clip)
            {
                last = new UiBatch { TextureId = textureId, WrapS = wrapS, WrapT = wrapT, Blend = blend, Clip = Clip, Start = Vertices.Count };
                Batches.Add(last);
            }
            Vertices.Add(a);
            Vertices.Add(b);
            Vertices.Add(c);
            last.Count += 3;
        }

        // many untextured, alpha-blended triangles at once (3 vertices each), e.g. a cached 3D view
        public void Triangles(List<UiVertex> vertices)
        {
            if (vertices.Count == 0) return;
            UiBatch? last = Batches.Count > 0 ? Batches[^1] : null;
            if (last == null || last.TextureId != -1 || last.WrapS != UiWrap.Clamp || last.WrapT != UiWrap.Clamp
                || last.Blend != UiBlend.Alpha || last.Clip != Clip)
            {
                last = new UiBatch { TextureId = -1, WrapS = UiWrap.Clamp, WrapT = UiWrap.Clamp, Blend = UiBlend.Alpha, Clip = Clip, Start = Vertices.Count };
                Batches.Add(last);
            }
            Vertices.AddRange(vertices);
            last.Count += vertices.Count;
        }

        // an axis-aligned quad (two triangles), e.g. a text glyph or a solid fill
        public void Quad(int textureId, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
            float r, float g, float b, float a, UiBlend blend = UiBlend.Alpha)
        {
            var p0 = new UiVertex(x0, y0, u0, v0, r, g, b, a);
            var p1 = new UiVertex(x1, y0, u1, v0, r, g, b, a);
            var p2 = new UiVertex(x1, y1, u1, v1, r, g, b, a);
            var p3 = new UiVertex(x0, y1, u0, v1, r, g, b, a);
            Triangle(textureId, UiWrap.Clamp, UiWrap.Clamp, blend, p0, p1, p2);
            Triangle(textureId, UiWrap.Clamp, UiWrap.Clamp, blend, p0, p2, p3);
        }
    }
}
