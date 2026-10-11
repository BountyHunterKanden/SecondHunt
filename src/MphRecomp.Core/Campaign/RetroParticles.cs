using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Prime 1 particle effects (PART, parsed by Core Import/Retro/Part.cs) run the way the game runs them: Metaforce's
    // CElementGen and its element classes (a reverse-engineered Prime 1 engine, https://github.com/AxioDL/metaforce, MIT License,
    // Copyright (c) Metaforce Contributors). 60 frames a second; GRTE particles a frame (the remainder carried), up to MAXP, emission ending
    // at PSLT or when the owner stops it; each particle lives LTME frames (taken at birth), its SIZE (full billboard
    // width) / ROTA (degrees) / COLR evaluated by its own age; AAPH = additive; KSSM children start at their frame with
    // the parent's transform and stop emitting with it. Draw-only and only what the effects we import use: no lights
    // (MPH's renderer has none), no line / motion-blur / particle-model / swoosh / electric kinds, no collision spawns.
    // Effect space = Prime's metres, Z up; Transform takes it to the world (scale included, OpenTK row vectors).
    public sealed class RetroParticles
    {
        readonly Dictionary<uint, Part> _parts = new();
        readonly Dictionary<uint, (int W, int H)> _texSize = new();

        public Part? Get(uint id) => _parts.TryGetValue(id, out Part? p) ? p : null;
        public int Count => _parts.Count;
        public void Add(uint id, Part p) => _parts[id] = p;
        // a texture's size (a flipbook's tiles need it)
        public void SetTextureSize(uint id, int w, int h) => _texSize[id] = (w, h);
        public (int W, int H)? TextureSize(uint id) => _texSize.TryGetValue(id, out (int, int) s) ? s : null;

        // every "<hex id>.part" in a folder (what Import/Effects/BodyFx.cs writes)
        public static RetroParticles Load(string dir)
        {
            var lib = new RetroParticles();
            if (!Directory.Exists(dir)) return lib;
            foreach (string f in Directory.GetFiles(dir, "*.part"))
            {
                if (UInt32.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out uint id))
                {
                    lib.Add(id, Part.Load(File.ReadAllBytes(f)));
                }
            }
            // "<hex id>.png": the size from its header (IHDR: width, height, big-endian, at 16)
            foreach (string f in Directory.GetFiles(dir, "*.png"))
            {
                if (UInt32.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out uint id))
                {
                    var head = new byte[24];
                    using FileStream fs = File.OpenRead(f);
                    if (fs.Read(head, 0, 24) == 24)
                    {
                        lib.SetTextureSize(id, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16)),
                            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20)));
                    }
                }
            }
            return lib;
        }
    }

    // one particle to draw: a camera-facing square
    public struct RetroBillboard
    {
        public Vector3 Position;    // world
        public float Size;          // world full width
        public float Rotation;      // degrees
        public Vector4 Color;       // RGBA, 0..1 (times the texture)
        public uint Texture;        // TXTR id
        public Vector4 Uv;          // u0, v0, u1, v1 of the texture (v 0: its top row); all zero: the whole texture
        public bool Additive;
        public bool DepthWrite;
    }

    public sealed class RetroParticleSystem
    {
        sealed class Particle
        {
            public Vector3 Pos, Vel;    // world (Follow: effect space)
            public int Age, Life;
            public readonly Dictionary<Part.Elem, float> Init = new(ReferenceEqualityComparer.Instance);
        }

        readonly RetroParticles _lib;
        readonly Part _part;
        readonly Random _rand;
        readonly List<Particle> _live = new();
        readonly List<RetroParticleSystem> _kids = new();
        readonly RetroParticleSystem? _parent;
        float _remainder;
        int _frame;
        bool _emitting = true;

        public uint Id { get; }
        // effect space -> world; set every frame for an effect riding a bone (Follow or not: new particles start there)
        public Matrix4 Transform;
        // true: the whole system moves with Transform (Prime's "continuous system"); false: particles stay where born
        public readonly bool Follow;

        public RetroParticleSystem(RetroParticles lib, uint id, Matrix4 transform, bool follow, int seed = 0)
            : this(lib, lib.Get(id) ?? throw new InvalidDataException($"particle effect {id:x8} not imported"), id, transform,
                  follow, new Random(seed == 0 ? (int)id : seed), null)
        {
        }

        RetroParticleSystem(RetroParticles lib, Part part, uint id, Matrix4 transform, bool follow, Random rand,
            RetroParticleSystem? parent)
        {
            _lib = lib; _part = part; Id = id; Transform = transform; Follow = follow; _rand = rand; _parent = parent;
        }

        // the owner stops emission (the event's frames are up); children stop with it
        public bool Emitting
        {
            get => _emitting && (_parent?.Emitting ?? true);
            set => _emitting = value;
        }

        public int Frame => _frame;

        public int LiveParticles
        {
            get
            {
                int n = _live.Count;
                foreach (RetroParticleSystem k in _kids) n += k.LiveParticles;
                return n;
            }
        }

        // nothing left to emit or draw
        public bool Done
        {
            get
            {
                if (_live.Count > 0) return false;
                foreach (RetroParticleSystem k in _kids)
                {
                    if (!k.Done) return false;
                }
                if (!Emitting) return true;
                foreach (Part.Spawn s in _part.Spawns)
                {
                    if (s.Frame >= _frame) return false;
                }
                return _frame >= SysInt("PSLT", Int32.MaxValue);
            }
        }

        // one 60 Hz frame
        public void Step()
        {
            foreach (Part.Spawn s in _part.Spawns)
            {
                if (s.Frame == _frame && _lib.Get(s.Part) is Part kid)
                {
                    _kids.Add(new RetroParticleSystem(_lib, kid, s.Part, Transform, Follow, _rand, this));
                }
            }
            foreach (RetroParticleSystem k in _kids) k.Transform = Transform;
            // age and move the live ones, then add this frame's new ones
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Particle p = _live[i];
                p.Age++;
                if (p.Age >= p.Life)
                {
                    _live.RemoveAt(i);
                    continue;
                }
                p.Vel += ModVelocity(p);
                p.Pos += p.Vel;
            }
            if (Emitting && _frame < SysInt("PSLT", Int32.MaxValue))
            {
                _remainder += SysReal("GRTE", 1);
                int n = (int)MathF.Floor(_remainder);
                _remainder -= n;
                int max = SysInt("MAXP", 256);
                for (int i = 0; i < n && _live.Count < max; i++) Emit();
            }
            foreach (RetroParticleSystem k in _kids) k.Step();
            _kids.RemoveAll(k => k.Done);
            _frame++;
        }

        void Emit()
        {
            var p = new Particle();
            var c = new Ctx(p, _frame, 1);
            p.Life = Math.Max(1, EvalInt(_part["LTME"], c, 1));
            c.Life = p.Life;
            Vector3 pos = Vector3.Zero, vel = Vector3.Zero;
            Part.Elem? em = _part["EMTR"];
            if (em != null)
            {
                switch (em.Kind)
                {
                    case "SEMR":
                        pos = EvalVec(em.Kid(0), c);
                        vel = EvalVec(em.Kid(1), c);
                        break;
                    case "SPHE":
                    case "ASPH":
                    {
                        Vector3 centre = EvalVec(em.Kid(0), c);
                        float radius = EvalReal(em.Kid(1), c, 0), speed = EvalReal(em.Kid(2), c, 0);
                        Vector3 dir = RandomDir();
                        pos = centre + dir * radius;
                        vel = dir * speed;
                        break;
                    }
                }
            }
            if (Follow)
            {
                p.Pos = pos; p.Vel = vel;
            }
            else
            {
                p.Pos = Vector3.TransformPosition(pos, Transform);
                p.Vel = Vector3.TransformVector(vel, Transform);
            }
            _live.Add(p);
        }

        Vector3 RandomDir()
        {
            while (true)
            {
                var v = new Vector3(Rnd() * 2 - 1, Rnd() * 2 - 1, Rnd() * 2 - 1);
                float l = v.LengthSquared;
                if (l > 1e-4f && l <= 1) return v / MathF.Sqrt(l);
            }
        }

        static readonly string[] ModTags = { "PSVM", "VEL1", "VEL2", "VEL3", "VEL4" };

        // PSVM / VEL1-4: the constant, gravity and wind kinds (the others are left out)
        Vector3 ModVelocity(Particle p)
        {
            Vector3 d = Vector3.Zero;
            Ctx? c = null;
            foreach (string tag in ModTags)
            {
                Part.Elem? m = _part[tag];
                if (m == null) continue;
                c ??= new Ctx(p, p.Age, p.Life);
                Vector3 v;
                switch (m.Kind)
                {
                    case "CNST": v = new Vector3(EvalReal(m.Kid(0), c, 0), EvalReal(m.Kid(1), c, 0), EvalReal(m.Kid(2), c, 0)); break;
                    case "GRAV": v = EvalVec(m.Kid(0), c); break;
                    case "WIND":
                    {
                        Vector3 w = EvalVec(m.Kid(0), c);
                        float f = EvalReal(m.Kid(1), c, 0);
                        Vector3 cur = Follow ? p.Vel : Vector3.TransformVector(p.Vel, Transform.Inverted());
                        v = (w - cur) * f;
                        break;
                    }
                    default: continue;
                }
                d += Follow ? v : Vector3.TransformVector(v, Transform);
            }
            return d;
        }

        // the billboards to draw this frame (children included)
        public void Collect(List<RetroBillboard> into)
        {
            Part.Elem? tex = _part["TEXR"];
            if (tex != null && tex.Id != 0 && _live.Count > 0)
            {
                float scale = Transform.Row0.Xyz.Length;
                bool additive = _part.Flag("AAPH");
                bool zbuf = _part.Flag("ZBUF");
                Part.Elem? pofsE = _part["POFS"];
                FlipbookSetup(tex, out int tilesX, out int tiles, out Vector4 tileSize, out Vector2 stride, out int cycle, out bool loop);
                foreach (Particle p in _live)
                {
                    var c = new Ctx(p, p.Age, p.Life);
                    float size = EvalReal(_part["SIZE"], c, 0.1f);
                    if (size <= 0.0005f) continue;
                    Vector4 col = EvalColor(_part["COLR"], c);
                    if (col.W <= 0.002f) continue;
                    Vector3 pos = Follow ? Vector3.TransformPosition(p.Pos, Transform) : p.Pos;
                    if (pofsE != null) pos += Vector3.TransformVector(EvalVec(pofsE, c), Transform);
                    into.Add(new RetroBillboard
                    {
                        Position = pos,
                        Size = size * scale,
                        Rotation = EvalReal(_part["ROTA"], c, 0),
                        Color = col,
                        Texture = tex.Id,
                        Uv = tiles > 0 ? Flipbook(p.Age, tilesX, tiles, tileSize, stride, cycle, loop) : default,
                        Additive = additive,
                        DepthWrite = zbuf,
                    });
                }
            }
            foreach (RetroParticleSystem k in _kids) k.Collect(into);
        }

        // ATEX (Metaforce's CUVEAnimTexture): the texture cut into tiles (stride apart, tile-sized), counted from its
        // bottom-left tile along the row and up; a particle shows tile age * tiles / cycle (looping, or holding the last)
        void FlipbookSetup(Part.Elem tex, out int tilesX, out int tiles, out Vector4 tileSize, out Vector2 stride, out int cycle,
            out bool loop)
        {
            tilesX = tiles = cycle = 0;
            tileSize = default;
            stride = default;
            loop = false;
            if (tex.Kind != "ATEX" || _lib.TextureSize(tex.Id) is not (int w, int h) || w <= 0 || h <= 0) return;
            var c = new Ctx(null, 0, 1);
            int tw = EvalInt(tex.Kid(0), c, w), th = EvalInt(tex.Kid(1), c, h);
            int sw = Math.Max(1, EvalInt(tex.Kid(2), c, w)), sh = Math.Max(1, EvalInt(tex.Kid(3), c, h));
            tilesX = Math.Max(1, w / sw);
            int tilesY = Math.Max(1, h / sh);
            tiles = tilesX * tilesY;
            tileSize = new Vector4(tw / (float)w, th / (float)h, 0, tilesY);
            stride = new Vector2(sw / (float)w, sh / (float)h);
            cycle = Math.Max(1, EvalInt(tex.Kid(4), c, 1));
            loop = tex.Kid(5)?.B ?? false;
        }

        static Vector4 Flipbook(int age, int tilesX, int tiles, Vector4 tileSize, Vector2 stride, int cycle, bool loop)
        {
            int f = loop ? age % cycle : Math.Min(age, cycle - 1);
            int tile = Math.Clamp(f * tiles / cycle, 0, tiles - 1);
            int x = tile % tilesX, y = (int)tileSize.W - 1 - tile / tilesX;
            float u0 = x * stride.X, v0 = y * stride.Y;
            return new Vector4(u0, v0, u0 + tileSize.X, v0 + tileSize.Y);
        }

        // ---- element evaluation ----

        // Frame: the particle's age (system-level values: the system's frame); Life: the particle's lifetime
        sealed class Ctx
        {
            public readonly Particle? P;    // null: a system-level value
            public readonly int Frame;
            public int Life;
            public Ctx(Particle? p, int frame, int life) { P = p; Frame = frame; Life = life; }
        }

        float Rnd() => (float)_rand.NextDouble();

        int SysInt(string tag, int dflt) => EvalInt(_part[tag], new Ctx(null, _frame, 1), dflt);
        float SysReal(string tag, float dflt) => EvalReal(_part[tag], new Ctx(null, _frame, 1), dflt);

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // IRND: once per particle (system-level: each time)
        float Initial(Part.Elem e, Ctx c, Func<float> make)
        {
            if (c.P == null) return make();
            if (!c.P.Init.TryGetValue(e, out float v)) c.P.Init[e] = v = make();
            return v;
        }

        // KEYP: 100 keys by lifetime percent, lerped; KEYE: a key per frame, looping LoopStart..LoopEnd
        static float Keyed(Part.Elem e, Ctx c, int width, int lane)
        {
            float[] k = e.Keys!;
            int n = k.Length / width;
            if (n == 0) return 0;
            if (e.Percent)
            {
                float pct = c.Frame * 100f / Math.Max(1, c.Life);
                int i = (int)pct;
                if (i >= n - 1) return k[(n - 1) * width + lane];
                return Lerp(k[i * width + lane], k[(i + 1) * width + lane], pct - i);
            }
            int f = c.Frame;
            if (e.Loop && f >= e.LoopEnd && e.LoopEnd > e.LoopStart) f = (f - e.LoopStart) % (e.LoopEnd - e.LoopStart) + e.LoopStart;
            f = Math.Clamp(f, 0, n - 1);
            return k[f * width + lane];
        }

        // PULS(a, b, A, B): A for a + 1 frames, then B for b + 1
        bool PulseA(Part.Elem e, Ctx c)
        {
            int a = EvalInt(e.Kid(0), c, 0), b = EvalInt(e.Kid(1), c, 0);
            return c.Frame % Math.Max(1, a + b + 2) <= a;
        }

        int EvalInt(Part.Elem? e, Ctx c, int dflt)
        {
            if (e == null) return dflt;
            switch (e.Kind)
            {
                case "CNST": return e.I;
                case "KEYE": case "KEYP": return (int)Keyed(e, c, 1, 0);
                case "RAND": { int a = EvalInt(e.Kid(0), c, 0), b = EvalInt(e.Kid(1), c, 0); return a + (int)(Rnd() * (b - a + 1)); }
                case "IRND":
                {
                    int a = EvalInt(e.Kid(0), c, 0), b = EvalInt(e.Kid(1), c, 0);
                    return (int)Initial(e, c, () => a + (int)(Rnd() * (b - a + 1)));
                }
                case "ADD_": return EvalInt(e.Kid(0), c, 0) + EvalInt(e.Kid(1), c, 0);
                case "SUB_": return EvalInt(e.Kid(0), c, 0) - EvalInt(e.Kid(1), c, 0);
                case "MULT": return EvalInt(e.Kid(0), c, 0) * EvalInt(e.Kid(1), c, 0);
                case "PULS": return PulseA(e, c) ? EvalInt(e.Kid(2), c, 0) : EvalInt(e.Kid(3), c, 0);
                case "CLMP": return Math.Clamp(EvalInt(e.Kid(2), c, 0), EvalInt(e.Kid(0), c, 0), EvalInt(e.Kid(1), c, 0));
                case "TSCL": return (int)(EvalReal(e.Kid(0), c, 0) * c.Frame);
                case "RTOI": return (int)(EvalReal(e.Kid(0), c, 0) * EvalReal(e.Kid(1), c, 1));
                case "ILPT": return (int)(EvalInt(e.Kid(0), c, 0) / 100f * c.Life);
                case "IMPL": return EvalInt(e.Kid(0), c, 0);
            }
            return dflt;
        }

        float EvalReal(Part.Elem? e, Ctx c, float dflt)
        {
            if (e == null) return dflt;
            switch (e.Kind)
            {
                case "CNST": return e.F;
                case "KEYE": case "KEYP": return Keyed(e, c, 1, 0);
                case "RAND": return Lerp(EvalReal(e.Kid(0), c, 0), EvalReal(e.Kid(1), c, 0), Rnd());
                case "IRND":
                {
                    float a = EvalReal(e.Kid(0), c, 0), b = EvalReal(e.Kid(1), c, 0);
                    return Initial(e, c, () => Lerp(a, b, Rnd()));
                }
                case "LFTW": case "ISWT":
                    return Lerp(EvalReal(e.Kid(0), c, 0), EvalReal(e.Kid(1), c, 0), Math.Min(1f, c.Frame / (float)Math.Max(1, c.Life)));
                case "RLPT": return Math.Max(0, EvalReal(e.Kid(0), c, 0)) / 100f * c.Life;
                case "SCAL": return EvalReal(e.Kid(0), c, 0) * c.Frame;
                case "PULS": return PulseA(e, c) ? EvalReal(e.Kid(2), c, 0) : EvalReal(e.Kid(3), c, 0);
                case "ADD_": return EvalReal(e.Kid(0), c, 0) + EvalReal(e.Kid(1), c, 0);
                case "SUB_": return EvalReal(e.Kid(0), c, 0) - EvalReal(e.Kid(1), c, 0);
                case "MULT": return EvalReal(e.Kid(0), c, 0) * EvalReal(e.Kid(1), c, 0);
                case "CLMP": return Math.Clamp(EvalReal(e.Kid(2), c, 0), EvalReal(e.Kid(0), c, 0), EvalReal(e.Kid(1), c, 0));
                case "SINE":
                {
                    // sin(frame * freq + phase) * amplitude, degrees
                    float freq = EvalReal(e.Kid(0), c, 0), amp = EvalReal(e.Kid(1), c, 0), phase = EvalReal(e.Kid(2), c, 0);
                    return MathF.Sin(MathHelper.DegreesToRadians(c.Frame * freq + phase)) * amp;
                }
                case "VMAG": return EvalVec(e.Kid(0), c).Length;
                case "VXTR": return EvalVec(e.Kid(0), c).X;
                case "VYTR": return EvalVec(e.Kid(0), c).Y;
                case "VZTR": return EvalVec(e.Kid(0), c).Z;
                case "CEXT": return EvalInt(e.Kid(0), c, 0);
                case "PSLL": case "PRLW": return c.Frame;
            }
            return dflt;
        }

        Vector3 EvalVec(Part.Elem? e, Ctx c)
        {
            if (e == null) return Vector3.Zero;
            switch (e.Kind)
            {
                case "CNST": return new Vector3(EvalReal(e.Kid(0), c, 0), EvalReal(e.Kid(1), c, 0), EvalReal(e.Kid(2), c, 0));
                case "KEYE": case "KEYP": return new Vector3(Keyed(e, c, 3, 0), Keyed(e, c, 3, 1), Keyed(e, c, 3, 2));
                case "ADD_": return EvalVec(e.Kid(0), c) + EvalVec(e.Kid(1), c);
                case "SUB_": return EvalVec(e.Kid(0), c) - EvalVec(e.Kid(1), c);
                case "MULT": return EvalVec(e.Kid(0), c) * EvalVec(e.Kid(1), c);
                case "RTOV": return new Vector3(EvalReal(e.Kid(0), c, 0));
                case "PULS": return PulseA(e, c) ? EvalVec(e.Kid(2), c) : EvalVec(e.Kid(3), c);
                case "ANGC":
                {
                    // RotateZ(yBase + yRange * (r - 0.5)) * RotateX(xBase + xRange * (r - 0.5)) * (0, mag, 0)
                    float xb = EvalReal(e.Kid(0), c, 0), yb = EvalReal(e.Kid(1), c, 0);
                    float xr = EvalReal(e.Kid(2), c, 0), yr = EvalReal(e.Kid(3), c, 0), mag = EvalReal(e.Kid(4), c, 0);
                    float ax = MathHelper.DegreesToRadians(xb + xr * (Rnd() - 0.5f));
                    float az = MathHelper.DegreesToRadians(yb + yr * (Rnd() - 0.5f));
                    float y = mag * MathF.Cos(ax), z = mag * MathF.Sin(ax);
                    return new Vector3(-y * MathF.Sin(az), y * MathF.Cos(az), z);
                }
                case "CONE":
                {
                    // around a direction, spread by a fraction of a half sphere
                    Vector3 dir = EvalVec(e.Kid(0), c);
                    float spread = EvalReal(e.Kid(1), c, 0);
                    float len = dir.Length;
                    if (len < 1e-6f) return Vector3.Zero;
                    return Vector3.Normalize(dir / len + RandomDir() * Math.Clamp(spread, 0, 1)) * len;
                }
            }
            return Vector3.Zero;
        }

        Vector4 EvalColor(Part.Elem? e, Ctx c)
        {
            if (e == null) return Vector4.One;
            switch (e.Kind)
            {
                case "CNST":
                    return new Vector4(EvalReal(e.Kid(0), c, 0), EvalReal(e.Kid(1), c, 0), EvalReal(e.Kid(2), c, 0), EvalReal(e.Kid(3), c, 0));
                case "KEYE": case "KEYP":
                    return new Vector4(Keyed(e, c, 4, 0), Keyed(e, c, 4, 1), Keyed(e, c, 4, 2), Keyed(e, c, 4, 3));
                case "FADE":
                {
                    Vector4 a = EvalColor(e.Kid(0), c), b = EvalColor(e.Kid(1), c);
                    float end = EvalReal(e.Kid(2), c, 0);
                    float t = end > 0 ? c.Frame / end : 1;
                    return t > 1 ? b : Vector4.Lerp(a, b, t);
                }
                case "CFDE":
                {
                    Vector4 a = EvalColor(e.Kid(0), c), b = EvalColor(e.Kid(1), c);
                    float s = EvalReal(e.Kid(2), c, 0), end = EvalReal(e.Kid(3), c, 0);
                    if (c.Frame < s) return a;
                    if (c.Frame > end) return b;
                    return Vector4.Lerp(a, b, (c.Frame - s) / Math.Max(1e-6f, end - s));
                }
                case "PULS": return PulseA(e, c) ? EvalColor(e.Kid(2), c) : EvalColor(e.Kid(3), c);
                case "CHAN":
                {
                    int sw = EvalInt(e.Kid(2), c, 0);
                    return c.Frame < sw ? EvalColor(e.Kid(0), c) : EvalColor(e.Kid(1), c);
                }
            }
            return Vector4.One;
        }
    }
}
