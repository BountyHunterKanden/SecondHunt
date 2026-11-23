using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Retro
{
    // Echoes animation events. Echoes ANIMs carry no EVNT id and the ANCS lists no resources; the event sets sit inline
    // at the end of the ANCS, right after the half-transitions (Ancs.AnimSet.EventsAt). Read from the disc:
    //   u32 count; per set: u32 version (2), u32 loops, users, particles, sounds, each POI list in that order.
    //   POI base: u16 version, cstring name, u16 type, f32 time, u32 state, u32 index, u8 unique, f32 weight,
    //             u32 character, u32 flags
    //   loop: base + u8 | user: base + u32 user type + cstring bone
    //   particle: base + u32 frames + "PART" + u32 id + u32 bone id (a number, unlike MP1's name) + f32 scale + u32 mode
    //   sound: base + u32 sound id + f32 falloff + f32 max distance + 12 bytes (u32, u16, u16, u32)
    // Set i belongs to the meta-animations whose play leaf has primitive id i.
    public sealed class EchoesEvents
    {
        public sealed class Poi
        {
            public string Name = "";
            public ushort Type;
            public float Time;
            public uint State, Index, Character, Flags;
            public byte Unique;
            public float Weight;
        }

        public sealed class Loop { public Poi Base = new(); public byte Value; }
        public sealed class User { public Poi Base = new(); public uint UserType; public string Bone = ""; }
        public sealed class Particle { public Poi Base = new(); public uint Frames, Part, Bone, Mode; public float Scale; }
        public sealed class Sound { public Poi Base = new(); public uint Id; public float Falloff, MaxDistance; }

        public sealed class Set
        {
            public readonly List<Loop> Loops = new();
            public readonly List<User> Users = new();
            public readonly List<Particle> Particles = new();
            public readonly List<Sound> Sounds = new();
            public bool Empty => Loops.Count + Users.Count + Particles.Count + Sounds.Count == 0;
        }

        public readonly List<Set> Sets = new();
        public int End;   // where reading stopped (the end of the file for every ANCS read so far)

        public static EchoesEvents Read(byte[] b, int at)
        {
            var ev = new EchoesEvents();
            int o = at;
            uint n = U32(b, ref o);
            if (n > 4096) throw new InvalidDataException($"ANCS events: {n} sets at {at}");
            for (uint i = 0; i < n; i++)
            {
                var set = new Set();
                uint ver = U32(b, ref o);
                if (ver != 2) throw new InvalidDataException($"ANCS events: set {i} version {ver} at {o - 4}");
                uint nl = U32(b, ref o);
                for (uint k = 0; k < nl; k++) set.Loops.Add(new Loop { Base = Base(b, ref o), Value = b[o++] });
                uint nu = U32(b, ref o);
                for (uint k = 0; k < nu; k++)
                {
                    var u = new User { Base = Base(b, ref o) };
                    u.UserType = U32(b, ref o);
                    u.Bone = Str(b, ref o);
                    set.Users.Add(u);
                }
                uint np = U32(b, ref o);
                for (uint k = 0; k < np; k++)
                {
                    var p = new Particle { Base = Base(b, ref o) };
                    p.Frames = U32(b, ref o);
                    uint tag = U32(b, ref o);
                    if (tag != 0x50415254) throw new InvalidDataException($"ANCS events: set {i} particle {k} tag {tag:x8}");
                    p.Part = U32(b, ref o);
                    p.Bone = U32(b, ref o);
                    p.Scale = Be.F32(b, o); o += 4;
                    p.Mode = U32(b, ref o);
                    set.Particles.Add(p);
                }
                uint ns = U32(b, ref o);
                for (uint k = 0; k < ns; k++)
                {
                    var s = new Sound { Base = Base(b, ref o) };
                    s.Id = U32(b, ref o);
                    s.Falloff = Be.F32(b, o); o += 4;
                    s.MaxDistance = Be.F32(b, o); o += 4;
                    o += 12;
                    set.Sounds.Add(s);
                }
                ev.Sets.Add(set);
            }
            ev.End = o;
            return ev;
        }

        // the set each named meta-animation plays (by its first play leaf's primitive id), or null
        public Set? For(Ancs.MetaAnim meta)
        {
            foreach (Ancs.MetaAnim p in Ancs.Prims(meta))
            {
                if (p.PrimId < Sets.Count) return Sets[(int)p.PrimId];
            }
            return null;
        }

        static uint U32(byte[] b, ref int o) { uint v = Be.U32(b, o); o += 4; return v; }

        static string Str(byte[] b, ref int o) { string v = Be.CString(b, o, out int e); o = e + 1; return v; }

        static Poi Base(byte[] b, ref int o)
        {
            var p = new Poi();
            o += 2;
            p.Name = Str(b, ref o);
            p.Type = Be.U16(b, o); o += 2;
            p.Time = Be.F32(b, o); o += 4;
            p.State = U32(b, ref o);
            p.Index = U32(b, ref o);
            p.Unique = b[o++];
            p.Weight = Be.F32(b, o); o += 4;
            p.Character = U32(b, ref o);
            p.Flags = U32(b, ref o);
            return p;
        }
    }
}
