using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import.Retro
{
    // A Retro particle generator (PART, "GPSM": Metroid Prime 1 / Echoes): a list of 4-character properties, each an
    // element tree (int / real / vector / colour / mod-vector / emitter / texture elements, every node a 4-character
    // class id and its children), read the way the game reads it: Metaforce's CParticleDataFactory (a reverse-engineered Prime 1
    // engine, https://github.com/AxioDL/metaforce, MIT License, Copyright (c) Metaforce Contributors). Only parsed here;
    // Core Campaign/RetroParticles.cs runs it.
    public sealed class Part
    {
        public const uint Magic = 0x4750534D;   // "GPSM"

        // what an element evaluates to
        public enum Cat : byte { Int, Real, Vector, Color, ModVector, Emitter, Texture, Bool, Asset }

        public sealed class Elem
        {
            public Cat Cat;
            public string Kind = "";        // "CNST", "RAND", ... (Bool: "BOOL"; Asset: the referenced type)
            public float F;                 // Real CNST
            public int I;                   // Int CNST
            public bool B;                  // Bool
            public uint Id;                 // Texture / Asset: the resource id (0 = none)
            public Elem?[] Kids = Array.Empty<Elem?>();
            // KEYE / KEYP: values (1, 3 or 4 floats per key; ints as floats), percent keys, loop + its range
            public float[]? Keys;
            public bool Percent, Loop;
            public int LoopStart, LoopEnd;
            public Elem? Kid(int i) => i < Kids.Length ? Kids[i] : null;
            public override string ToString() => Kind;
        }

        // KSSM: systems spawned at given frames of this one
        public readonly record struct Spawn(int Frame, uint Part);

        public readonly Dictionary<string, Elem?> Props = new(StringComparer.Ordinal);
        public readonly List<Spawn> Spawns = new();
        public int SpawnEndFrame;

        public Elem? this[string tag] => Props.TryGetValue(tag, out Elem? e) ? e : null;
        public bool Flag(string tag) => this[tag]?.B ?? false;

        // the resources this one needs: textures, child systems (PART / SWHC / ELSC), particle models
        public IEnumerable<(string Type, uint Id)> Dependencies()
        {
            var seen = new HashSet<(string, uint)>();
            foreach (Elem? e in Props.Values)
            {
                foreach ((string t, uint id) in Assets(e))
                {
                    if (id != 0 && id != UInt32.MaxValue && seen.Add((t, id))) yield return (t, id);
                }
            }
            foreach (Spawn s in Spawns)
            {
                if (s.Part != 0 && s.Part != UInt32.MaxValue && seen.Add(("PART", s.Part))) yield return ("PART", s.Part);
            }
        }

        static IEnumerable<(string, uint)> Assets(Elem? e)
        {
            if (e == null) yield break;
            if (e.Cat == Cat.Texture && e.Id != 0) yield return ("TXTR", e.Id);
            if (e.Cat == Cat.Asset && e.Id != 0) yield return (e.Kind, e.Id);
            foreach (Elem? k in e.Kids)
            {
                foreach ((string, uint) x in Assets(k)) yield return x;
            }
        }

        public static Part Load(byte[] b)
        {
            var r = new Reader(b);
            if (r.U32() != Magic) throw new InvalidDataException("not a particle generator (GPSM)");
            var p = new Part();
            while (true)
            {
                string tag = r.Tag();
                if (tag == "_END") break;
                if (tag == "KSSM")
                {
                    if (r.Tag() == "CNST")
                    {
                        r.U32(); r.U32();
                        p.SpawnEndFrame = r.I32();
                        r.U32();
                        int frames = r.I32();
                        for (int i = 0; i < frames; i++)
                        {
                            int frame = r.I32(), n = r.I32();
                            for (int j = 0; j < n; j++)
                            {
                                uint id = r.U32();
                                r.Skip(12);
                                p.Spawns.Add(new Spawn(frame, id));
                            }
                        }
                    }
                    continue;
                }
                p.Props[tag] = tag switch
                {
                    "PSIV" or "PSOV" or "ILOC" or "IVEC" or "POFS" or "PMOP" or "PMRT" or "PMSC" or "SSPO" or "SEPO" or "LOFF"
                        or "LDIR" => r.Vector(),
                    "PSVM" or "VEL1" or "VEL2" or "VEL3" or "VEL4" => r.Mod(),
                    "PSTS" or "GRTE" or "SIZE" or "ROTA" or "LENG" or "WIDT" or "LINT" or "LFOR" or "LSLA" or "ADV1" or "ADV2"
                        or "ADV3" or "ADV4" or "ADV5" or "ADV6" or "ADV7" or "ADV8" => r.Real(),
                    "PSLT" or "PSWT" or "MBSP" or "MAXP" or "LTME" or "SEED" or "NCSY" or "CSSD" or "NDSY" or "PISY" or "SISY"
                        or "SSSD" or "SESD" or "LTYP" or "LFOT" => r.Int(),
                    "LIT_" or "ORNT" or "RSOP" or "AAPH" or "ZBUF" or "SORT" or "MBLR" or "VMD1" or "VMD2" or "VMD3" or "VMD4"
                        or "CIND" or "PMAB" or "PMUS" or "PMOO" or "LINE" or "FXLL" or "OPTS" => r.Bool(),
                    "EMTR" => r.Emitter(),
                    "COLR" or "PMCL" or "LCLR" => r.Color(),
                    "TEXR" or "TIND" => r.Texture(),
                    "PMDL" => r.Asset("CMDL"),
                    "ICTS" or "IDTS" or "IITS" => r.Asset("PART"),
                    "SSWH" => r.Asset("SWHC"),
                    "SELC" => r.Asset("ELSC"),
                    _ => throw new InvalidDataException($"particle property {tag} at {r.O - 4:x} is not known"),
                };
            }
            return p;
        }

        // A swoosh (SWHC, "SWSH"): a ribbon trailing the system that owns it (a PART's SSWH), read like a PART: Metaforce's
        // CParticleSwooshDataFactory (same element grammar). Only parsed here; the Pirate Blaster's shot draws it (app
        // CampaignRenderer.BlasterFx.cs). Added for the Pirate Blaster thread, 2026-10-10.
        public sealed class Swoosh
        {
            public const uint Magic = 0x53575348;   // "SWSH"
            public readonly Dictionary<string, Elem?> Props = new(StringComparer.Ordinal);
            public Elem? this[string tag] => Props.TryGetValue(tag, out Elem? e) ? e : null;
            public bool Flag(string tag) => this[tag]?.B ?? false;

            public static Swoosh Load(byte[] b)
            {
                var r = new Reader(b);
                if (r.U32() != Magic) throw new InvalidDataException("not a swoosh (SWSH)");
                var w = new Swoosh();
                while (true)
                {
                    string tag = r.Tag();
                    if (tag == "_END") break;
                    w.Props[tag] = tag switch
                    {
                        "PSLT" or "LENG" or "SIDE" or "SPLN" or "TSPN" => r.Int(),
                        "TIME" or "LRAD" or "RRAD" or "IROT" or "ROTM" => r.Real(),
                        "COLR" => r.Color(),
                        "POFS" or "IVEL" or "NPOS" => r.Vector(),
                        "VELM" or "VLM2" => r.Mod(),
                        "TEXR" => r.Texture(),
                        "LLRD" or "CROS" or "VLS1" or "VLS2" or "SROT" or "WIRE" or "TEXW" or "AALP" or "ZBUF" or "ORNT" or "CRND"
                            => r.Bool(),
                        _ => throw new InvalidDataException($"swoosh property {tag} at {r.O - 4:x} is not known"),
                    };
                }
                return w;
            }

            public uint Texture => this["TEXR"]?.Id ?? 0;
        }

        sealed class Reader
        {
            readonly byte[] _b;
            public int O;
            public Reader(byte[] b) { _b = b; }
            public uint U32() { uint v = Be.U32(_b, O); O += 4; return v; }
            public int I32() { int v = Be.I32(_b, O); O += 4; return v; }
            public float F32() { float v = Be.F32(_b, O); O += 4; return v; }
            public void Skip(int n) => O += n;
            public string Tag() { string v = Encoding.Latin1.GetString(_b, O, 4); O += 4; return v; }

            static Elem E(Cat c, string k, params Elem?[] kids) => new() { Cat = c, Kind = k, Kids = kids };

            Elem Keys(Cat c, string k, int width)
            {
                var e = new Elem { Cat = c, Kind = k };
                e.Percent = U32() != 0;
                U32();
                e.Loop = _b[O] != 0; O += 2;
                e.LoopEnd = I32();
                e.LoopStart = I32();
                int n = I32();
                e.Keys = new float[n * width];
                for (int i = 0; i < e.Keys.Length; i++) e.Keys[i] = c == Cat.Int ? I32() : F32();
                return e;
            }

            public Elem? Bool()
            {
                Tag();
                var e = new Elem { Cat = Cat.Bool, Kind = "BOOL", B = _b[O] != 0 };
                O++;
                return e;
            }

            public Elem? Int()
            {
                string k = Tag();
                switch (k)
                {
                    case "NONE": return null;
                    case "CNST": return new Elem { Cat = Cat.Int, Kind = k, I = I32() };
                    case "KEYE": case "KEYP": return Keys(Cat.Int, k, 1);
                    case "TSCL": return E(Cat.Int, k, Real());
                    case "DETH": case "ADD_": case "MULT": case "MODU": case "RAND": case "IRND": case "SUB_":
                        return E(Cat.Int, k, Int(), Int());
                    case "CHAN": case "CLMP": case "SPAH": return E(Cat.Int, k, Int(), Int(), Int());
                    case "PULS": return E(Cat.Int, k, Int(), Int(), Int(), Int());
                    case "IMPL": case "ILPT": return E(Cat.Int, k, Int());
                    case "RTOI": return E(Cat.Int, k, Real(), Real());
                    case "GTCP": case "GAPC": case "GEMT": return E(Cat.Int, k);
                }
                throw new InvalidDataException($"int element {k} at {O - 4:x} is not known");
            }

            public Elem? Real()
            {
                string k = Tag();
                switch (k)
                {
                    case "NONE": return null;
                    case "CNST": return new Elem { Cat = Cat.Real, Kind = k, F = F32() };
                    case "KEYE": case "KEYP": return Keys(Cat.Real, k, 1);
                    case "SCAL": case "RLPT": return E(Cat.Real, k, Real());
                    case "SINE": case "CLMP": return E(Cat.Real, k, Real(), Real(), Real());
                    case "ADD_": case "MULT": case "RAND": case "IRND": case "LFTW": case "ISWT": case "SUB_":
                        return E(Cat.Real, k, Real(), Real());
                    case "DOTP": return E(Cat.Real, k, Vector(), Vector());
                    case "CHAN": return E(Cat.Real, k, Real(), Real(), Int());
                    case "PULS": return E(Cat.Real, k, Int(), Int(), Real(), Real());
                    case "PRLW": case "PSLL": case "PAP1": case "PAP2": case "PAP3": case "PAP4": case "PAP5": case "PAP6":
                    case "PAP7": case "PAP8": return E(Cat.Real, k);
                    case "VXTR": case "VYTR": case "VZTR": case "VMAG": return E(Cat.Real, k, Vector());
                    case "CLTN": case "CEQL": return E(Cat.Real, k, Real(), Real(), Real(), Real());
                    case "CRNG": return E(Cat.Real, k, Real(), Real(), Real(), Real(), Real());
                    case "CEXT": return E(Cat.Real, k, Int());
                    case "ITRL": return E(Cat.Real, k, Int(), Real());
                    case "GTCR": case "GTCG": case "GTCB": case "GTCA": return E(Cat.Real, k, Color());
                }
                throw new InvalidDataException($"real element {k} at {O - 4:x} is not known");
            }

            public Elem? Vector()
            {
                string k = Tag();
                switch (k)
                {
                    case "NONE": return null;
                    case "CNST": return E(Cat.Vector, k, Real(), Real(), Real());
                    case "KEYE": case "KEYP": return Keys(Cat.Vector, k, 3);
                    case "ANGC": return E(Cat.Vector, k, Real(), Real(), Real(), Real(), Real());
                    case "CONE": return E(Cat.Vector, k, Vector(), Real());
                    case "CIRC": return E(Cat.Vector, k, Vector(), Vector(), Real(), Real(), Real());
                    case "CCLU": return E(Cat.Vector, k, Vector(), Vector(), Int(), Real());
                    case "ADD_": case "MULT": case "SUB_": return E(Cat.Vector, k, Vector(), Vector());
                    case "CHAN": return E(Cat.Vector, k, Vector(), Vector(), Int());
                    case "PULS": return E(Cat.Vector, k, Int(), Int(), Vector(), Vector());
                    case "RTOV": return E(Cat.Vector, k, Real());
                    case "PLOC": case "PLCO": case "PVEL": case "PSOF": case "PSOU": case "PSOR": case "PSTR": return E(Cat.Vector, k);
                    case "CTVC": return E(Cat.Vector, k, Color());
                }
                throw new InvalidDataException($"vector element {k} at {O - 4:x} is not known");
            }

            public Elem? Color()
            {
                string k = Tag();
                switch (k)
                {
                    case "NONE": return null;
                    case "CNST": return E(Cat.Color, k, Real(), Real(), Real(), Real());
                    case "KEYE": case "KEYP": return Keys(Cat.Color, k, 4);
                    case "FADE": return E(Cat.Color, k, Color(), Color(), Real());
                    case "CFDE": return E(Cat.Color, k, Color(), Color(), Real(), Real());
                    case "CHAN": return E(Cat.Color, k, Color(), Color(), Int());
                    case "PULS": return E(Cat.Color, k, Int(), Int(), Color(), Color());
                    case "PCOL": return E(Cat.Color, k);
                }
                throw new InvalidDataException($"colour element {k} at {O - 4:x} is not known");
            }

            public Elem? Mod()
            {
                string k = Tag();
                switch (k)
                {
                    case "NONE": return null;
                    case "CNST": return E(Cat.ModVector, k, Real(), Real(), Real());
                    case "GRAV": case "SPOS": return E(Cat.ModVector, k, Vector());
                    case "WIND": return E(Cat.ModVector, k, Vector(), Real());
                    case "EXPL": return E(Cat.ModVector, k, Real(), Real());
                    case "CHAN": return E(Cat.ModVector, k, Mod(), Mod(), Int());
                    case "PULS": return E(Cat.ModVector, k, Int(), Int(), Mod(), Mod());
                    case "IMPL": case "LMPL": case "EMPL": return E(Cat.ModVector, k, Vector(), Real(), Real(), Real(), Bool());
                    case "SWRL": return E(Cat.ModVector, k, Vector(), Vector(), Real(), Real());
                    case "BNCE": return E(Cat.ModVector, k, Vector(), Vector(), Real(), Real(), Bool());
                }
                throw new InvalidDataException($"mod-vector element {k} at {O - 4:x} is not known");
            }

            public Elem? Emitter()
            {
                string k = Tag();
                switch (k)
                {
                    case "NONE": return null;
                    case "SETR":
                    {
                        // ILOC <vector> IVEC <vector>, read as the simple emitter
                        Elem? pos = null, vel = null;
                        if (Tag() == "ILOC")
                        {
                            pos = Vector();
                            if (Tag() == "IVEC") vel = Vector();
                        }
                        return E(Cat.Emitter, "SEMR", pos, vel);
                    }
                    case "SEMR": return E(Cat.Emitter, k, Vector(), Vector());
                    case "SPHE": return E(Cat.Emitter, k, Vector(), Real(), Real());
                    case "ASPH": return E(Cat.Emitter, k, Vector(), Real(), Real(), Real(), Real(), Real(), Real());
                }
                throw new InvalidDataException($"emitter element {k} at {O - 4:x} is not known");
            }

            public Elem? Texture()
            {
                string k = Tag();
                if (k == "NONE") return null;
                uint id = Tag() != "NONE" ? U32() : 0;
                if (k == "CNST") return new Elem { Cat = Cat.Texture, Kind = k, Id = id };
                if (k == "ATEX")
                {
                    var e = new Elem { Cat = Cat.Texture, Kind = k, Id = id };
                    e.Kids = new[] { Int(), Int(), Int(), Int(), Int(), Bool() };   // tile w, h, stride w, h, cycle frames, loop
                    return e;
                }
                throw new InvalidDataException($"texture element {k} at {O - 8:x} is not known");
            }

            public Elem? Asset(string type)
            {
                if (Tag() == "NONE") return null;
                return new Elem { Cat = Cat.Asset, Kind = type, Id = U32() };
            }
        }
    }

    // A Retro collision response table (CRSC, "CRSM"): the effects and sounds a weapon's hit plays per surface. Prime 1:
    // tagged entries, each "CNST" + an id (particle effects: PART, decals: DPSC, sounds: a sound id) until "_END". Only
    // the default effect (DEFS) and its sound (DSFX) are used for now.
    public sealed class Crsc
    {
        public readonly Dictionary<string, uint> Ids = new(StringComparer.Ordinal);
        public uint DefaultEffect => Ids.TryGetValue("DEFS", out uint v) ? v : 0;
        public uint DefaultSound => Ids.TryGetValue("DSFX", out uint v) ? v : UInt32.MaxValue;

        public static Crsc Load(byte[] b)
        {
            if (Encoding.Latin1.GetString(b, 0, 4) != "CRSM") throw new InvalidDataException("not a collision response table (CRSM)");
            var c = new Crsc();
            int o = 4;
            while (o + 4 <= b.Length)
            {
                string tag = Encoding.Latin1.GetString(b, o, 4); o += 4;
                if (tag == "_END") break;
                string kind = Encoding.Latin1.GetString(b, o, 4); o += 4;
                if (kind == "NONE") continue;
                c.Ids[tag] = Be.U32(b, o); o += 4;
            }
            return c;
        }
    }
}
