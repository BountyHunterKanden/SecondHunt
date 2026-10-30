using System;
using System.Collections.Generic;

namespace MphRecomp.Import.Beyond
{
    // A Metroid Prime 4: Beyond material instance (MATI, spec section 5): its parameter list (textures, colours, scalars).
    // The one chunk, MDAT, holds two GUIDs + 2 bytes; u32, u32, u32 n, n x 10 bytes; the animation curves (u8 groups, per
    // group a u8 mask, per set bit one curve: u32 keys, per key f32 f32 u8 a u8 b (+8 bytes when a = 5, +8 when b = 5),
    // then 11 bytes); u8 n, n x 32 bytes; then u32 parameter count and the parameters: tag, u8 format, value --
    //   2              one f32
    //   3, 4, 10, 12   four f32 (a colour or vector)
    //   5, 6, 7, 8     a texture: TXTR GUID, u32 UV set, GUID
    //   32, 48, ...    u8, u16, then format/16 x four f32
    // An unknown format ends the list there (the parameters before it are kept; Warnings says where).
    // Clean-room: written from docs/cleanroom/G_beyond_spec.md alone.
    public sealed class BeyondMaterialInstance
    {
        public sealed class Parameter
        {
            public string Tag = "";
            public int Format;
            public float[] Values = Array.Empty<float>();
            public Guid Texture;     // a texture parameter's TXTR id (Guid.Empty otherwise)
            public int UvSet;        // a texture parameter's UV set
            public Guid TextureExtra;
            public bool IsTexture => Format is >= 5 and <= 8;
        }

        public Guid Id;
        public string What = "";
        public readonly List<Parameter> Parameters = new();
        public readonly List<string> Warnings = new();

        public static BeyondMaterialInstance Read(BeyondArchive archive, Guid id)
        {
            var m = Parse(archive.ReadAsset(id), $"MATI {id}");
            m.Id = id;
            return m;
        }

        public static BeyondMaterialInstance Parse(byte[] asset, string what)
        {
            BeyondForm.Expect(asset, "MATI", what);
            var m = new BeyondMaterialInstance { What = what };
            BeyondChunk mdat = BeyondForm.Find(BeyondForm.Chunks(asset, what), "MDAT", what);
            var c = new BeyondCursor(asset, mdat.DataStart, mdat.End, what + " MDAT");
            c.Skip(32 + 2);
            c.Skip(8);
            int nx = c.Count(10, "header record");
            c.Skip(10L * nx);
            // curves: skipped
            int groups = c.U8();
            for (int g = 0; g < groups; g++)
            {
                int mask = c.U8();
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((mask & (1 << bit)) == 0) continue;
                    int keys = c.Count(10, "curve key");
                    for (int k = 0; k < keys; k++)
                    {
                        c.Skip(8);
                        int a = c.U8(), b = c.U8();
                        if (a == 5) c.Skip(8);
                        if (b == 5) c.Skip(8);
                    }
                    c.Skip(11);
                }
            }
            int ny = c.U8();
            c.Skip(32L * ny);
            int np = c.Count(5, "parameter");
            for (int i = 0; i < np; i++)
            {
                int at = c.Pos;
                var p = new Parameter { Tag = c.Tag(), Format = c.U8() };
                switch (p.Format)
                {
                    case 2:
                        p.Values = new[] { c.F32() };
                        break;
                    case 3: case 4: case 10: case 12:
                        p.Values = Floats(c, 4);
                        break;
                    case 5: case 6: case 7: case 8:
                        p.Texture = c.Guid();
                        p.UvSet = checked((int)c.U32());
                        p.TextureExtra = c.Guid();
                        break;
                    default:
                        if (p.Format >= 32 && p.Format % 16 == 0)
                        {
                            c.Skip(3);
                            p.Values = Floats(c, p.Format / 16 * 4);
                            break;
                        }
                        m.Warnings.Add($"{what}: parameter {i} ({p.Tag}) at 0x{at:x} has unknown format {p.Format}; {np - i} parameters not read");
                        return m;
                }
                m.Parameters.Add(p);
            }
            return m;
        }

        static float[] Floats(BeyondCursor c, int n)
        {
            var f = new float[n];
            for (int i = 0; i < n; i++) f[i] = c.F32();
            return f;
        }

        // the first parameter with that tag, or null
        public Parameter? Find(string tag)
        {
            foreach (Parameter p in Parameters)
            {
                if (p.Tag == tag) return p;
            }
            return null;
        }
    }
}
