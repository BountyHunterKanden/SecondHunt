using System;
using System.Collections.Generic;

namespace MphRecomp.Import.Retro
{
    // A Prime 1 area (MREA v0xF, GameCube) read into Mrea's sections, for Prime 1 rooms as multiplayer arenas (the Main
    // Quarry, docs/MP2_MULTIPLAYER_IMPORT.md). Laid out as Scly1.cs notes and PrimeWorldEditor's CAreaLoader
    // (ReadHeaderPrime / ReadGeometryPrime, MIT; layout only): magic, version, 3x4 transform, u32 models, u32 sections,
    // eight u32 section numbers (geometry, scripts, collision, unknown, lights, visibility, path, octree), the section
    // sizes from 0x60, then the data 32-aligned after them, uncompressed. Its differences from Echoes, under Prime1:
    //   - a world model is 7 sections (header, positions, normals, colours, UVs, short UVs, surface offsets) then its
    //     surfaces, no Echoes lookup sections after them;
    //   - a surface's header is 0x2C before its extra data (Echoes 0x30), materials are Prime's;
    //   - collision materials are 32-bit and the collision has no Echoes u16 array before its vertices;
    //   - scripts are Scly1's (by position, no property ids): ReadScripts1; ReadScripts returns none.
    // Geometry, collision and scripts are in world space like Echoes' arenas (the Main Quarry's area transform is a
    // translation the vertices already carry).
    public sealed partial class Mrea
    {
        public const uint Prime1Version = 0xF;

        public bool Prime1 { get; private set; }

        byte[] _raw = Array.Empty<byte>();

        static Mrea ReadPrime1(byte[] raw)
        {
            var a = new Mrea { Prime1 = true, _raw = raw };
            for (int i = 0; i < 12; i++) a.Transform[i] = Be.F32(raw, 8 + 4 * i);
            a.ModelCount = (int)Be.U32(raw, 0x38);
            int sectionCount = (int)Be.U32(raw, 0x3C);
            int Block(int i) => Be.I32(raw, 0x40 + 4 * i);
            a.GeometrySection = Block(0); a.ScriptSection = Block(1); a.CollisionSection = Block(2);
            a.LightSection = Block(4); a.VisiSection = Block(5); a.PathSection = Block(6); a.OctreeSection = Block(7);
            a.GeneratedSection = a.PtlaSection = a.EgmcSection = -1;
            int o = Align32(0x60 + 4 * sectionCount);
            a.Sections = new byte[sectionCount][];
            for (int i = 0; i < sectionCount; i++)
            {
                int size = (int)Be.U32(raw, 0x60 + 4 * i);
                if (o + size > raw.Length) throw new InvalidOperationException($"Prime 1 MREA: section {i} runs past the file");
                a.Sections[i] = Be.Slice(raw, o, size);
                o += size;
            }
            // SCLY: "SCLY", version, layer count
            byte[] scly = a.ScriptSection >= 0 && a.ScriptSection < sectionCount ? a.Sections[a.ScriptSection] : Array.Empty<byte>();
            a.LayerCount = scly.Length >= 12 && Be.U32(scly, 0) == 0x53434C59 ? (int)Be.U32(scly, 8) : 0;
            return a;
        }

        // a Prime 1 collision material as Echoes' bits (Collision.SurfaceOf, FlippedTri, ShootThru). Prime 1's own (Metaforce
        // EMaterialTypes; the Main Quarry's 18 materials agree): 1-8 Stone..Dirt, 9/10 lava, 11 Snow, 12/14 mud, 13 Halfpipe,
        // 15 Glass, 17 Sand, 18 shoot-through, 19 solid and 20 (on every triangle), 21 camera-through, 22 Wood, 23 Organic,
        // 25 flipped triangle, 26-28 see/scan/AI-through, 29-31 Ceiling/Wall/Floor
        static ulong Prime1Flags(uint f)
        {
            const uint Same = 0x1FEu | 1u << 11 | 1u << 13 | 1u << 17 | 7u << 21 | 7u << 29;
            ulong e = f & Same;
            if ((f & (1u << 12 | 1u << 14)) != 0) e |= 1UL << (int)Surface.Dirt;
            if ((f & 1u << 15) != 0) e |= 1UL << (int)Surface.Glass;
            if ((f & 1u << 18) != 0) e |= 1UL << (int)Surface.ShootThru;
            if ((f & 1u << 25) != 0) e |= FlippedTri;
            return e;
        }

        // the room's script objects (Prime 1 only; empty for Echoes)
        public List<Scly1.Obj> ReadScripts1() => Prime1 ? Scly1.ReadScripts(_raw) : new List<Scly1.Obj>();
    }
}
