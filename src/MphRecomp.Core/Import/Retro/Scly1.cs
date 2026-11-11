using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Retro
{
    // Prime 1 room scripts: the SCLY section of a GameCube Prime 1 MREA (v0xF) and the PatternedAI block that every
    // patterned enemy starts its properties with (health, contact damage, weapon vulnerabilities). Echoes rooms are
    // Mrea.cs; Prime 1 properties carry no ids, so they are read by position, as Metaforce's ScriptLoader /
    // CPatternedInfo / CDamageVulnerability read them (MIT; layout notes only, no code copied).
    //   MREA v0xF: magic, version, 3x4 transform, u32 models, u32 sections, u32 section numbers (geometry, scripts,
    //     collision, unknown, lights, visibility, path, octree), section sizes from 0x60, data 32-aligned after them.
    //   SCLY: "SCLY", u32 version, u32 layers, u32 layer sizes; a layer is a u8, u32 objects, then each object: u8 type,
    //     u32 size, u32 id, u32 connections (state, message, target: 12 bytes each), u32 property count, properties
    //     (the first is the object's name string).
    //   PatternedInfo: u32 property count (35-38), 13 floats (mass, speed, turn speed, ranges, attack times, leash),
    //     contact DamageInfo (u32 count, u32 weapon, f32 damage, f32 radius, f32 knockback), f32 damage wait,
    //     HealthInfo (u32 count, f32 health, f32 knockback resistance), DamageVulnerability (u32 count, then one u32
    //     EVulnerability per weapon starting Power, Ice, Wave, Plasma, Bomb, Power Bomb, Missile, Boost Ball, Phazon).
    // Added for the enemy-import thread (docs/ENEMY_IMPORT.md 11.2 item 2), 2026-10-30.
    public static class Scly1
    {
        public const uint Prime1Version = 0xF;

        public sealed class Obj
        {
            public byte Type;
            public uint Id;
            public int Layer, PropCount;
            public string Name = "";
            public readonly List<(uint State, uint Message, uint Target)> Connections = new();
            public byte[] Props = Array.Empty<byte>();   // the property bytes, from the name on
        }

        public enum Vuln { Weak, Normal, Deflect, Immune, PassThrough, DirectWeak, DirectNormal, DirectImmune }
        public static readonly string[] VulnWeapons = { "Power", "Ice", "Wave", "Plasma", "Bomb", "PowerBomb", "Missile", "BoostBall", "Phazon" };

        public sealed record Patterned(int Offset, int PropCount, float Mass, float Speed, float DetectionRange,
            float Health, float KnockbackResist, uint ContactWeapon, float ContactDamage, float ContactRadius, Vuln[] Vulns);

        public static bool IsPrime1Area(byte[] raw) => raw.Length > 0x60 && Be.U32(raw, 0) == Mrea.Magic && Be.U32(raw, 4) == Prime1Version;

        // the SCLY section's bytes (offset into raw), or -1
        public static int ScriptSectionOffset(byte[] raw)
        {
            int sections = (int)Be.U32(raw, 0x3C), script = Be.I32(raw, 0x44);
            if (script < 0 || script >= sections) return -1;
            int o = (0x60 + 4 * sections + 31) & ~31;
            for (int i = 0; i < script; i++) o += (int)Be.U32(raw, 0x60 + 4 * i);
            return o + 4 <= raw.Length && Be.U32(raw, o) == 0x53434C59 ? o : -1;   // "SCLY"
        }

        public static List<Obj> ReadScripts(byte[] raw)
        {
            if (!IsPrime1Area(raw)) throw new InvalidDataException("not a Prime 1 MREA");
            int sc = ScriptSectionOffset(raw);
            if (sc < 0) throw new InvalidDataException("Prime 1 MREA: no SCLY at its script section");
            var res = new List<Obj>();
            int layers = (int)Be.U32(raw, sc + 8), lo = sc + 12 + 4 * layers;
            for (int l = 0; l < layers; l++)
            {
                int lsize = (int)Be.U32(raw, sc + 12 + 4 * l), end = lo + lsize;
                int count = (int)Be.U32(raw, lo + 1), q = lo + 5;
                for (int k = 0; k < count; k++)
                {
                    int size = (int)Be.U32(raw, q + 1), body = q + 5, bodyEnd = body + size;
                    if (bodyEnd > end) throw new InvalidDataException($"SCLY layer {l}: object {k} runs past its layer");
                    var obj = new Obj { Type = raw[q], Id = Be.U32(raw, body), Layer = l };
                    int conns = (int)Be.U32(raw, body + 4), p = body + 8;
                    for (int c = 0; c < conns; c++, p += 12)
                        obj.Connections.Add((Be.U32(raw, p), Be.U32(raw, p + 4), Be.U32(raw, p + 8)));
                    obj.PropCount = (int)Be.U32(raw, p); p += 4;
                    obj.Props = Be.Slice(raw, p, bodyEnd - p);
                    obj.Name = Be.CString(obj.Props, 0, out _);
                    res.Add(obj);
                    q = bodyEnd;
                }
                if (q > end) throw new InvalidDataException($"SCLY layer {l}: objects overrun the layer");
                lo = end;
            }
            return res;
        }

        // the object's PatternedAI block, found by its shape (types differ in what comes before it), or null
        public static Patterned? FindPatterned(Obj o)
        {
            byte[] b = o.Props;
            Be.CString(b, 0, out int e);
            // byte steps: a 1-byte bool can sit before the block
            for (int p = e + 1; p + 0x84 <= b.Length; p++)
            {
                uint pc = Be.U32(b, p);
                if (pc < 35 || pc > 38) continue;
                int di = p + 4 + 13 * 4, hi = di + 20 + 4, vi = hi + 12;
                if (Be.U32(b, di) != 4 || Be.U32(b, hi) != 2) continue;
                float hp = Be.F32(b, hi + 4), mass = Be.F32(b, p + 4);
                if (!(hp >= 0 && hp < 1e7) || !(mass >= 0 && mass < 1e6)) continue;
                int vc = (int)Be.U32(b, vi);
                if (vc < VulnWeapons.Length || vc > 40 || vi + 4 + 4 * VulnWeapons.Length > b.Length) continue;
                var v = new Vuln[VulnWeapons.Length];
                bool ok = true;
                for (int i = 0; i < v.Length && ok; i++) { uint x = Be.U32(b, vi + 4 + 4 * i); ok = x <= 7; v[i] = (Vuln)x; }
                if (!ok) continue;
                return new Patterned(p, (int)pc, mass, Be.F32(b, p + 8), Be.F32(b, p + 16), hp, Be.F32(b, hi + 8),
                    Be.U32(b, di + 4), Be.F32(b, di + 8), Be.F32(b, di + 12), v);
            }
            return null;
        }
    }
}
