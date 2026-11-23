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
        // Corruption effects run by Retro's own rules, as Echoes' are (RetroParticleSystem.Retro; the Hyper Beam's)
        public bool RetroRules;
        // Prime 1 effects by Retro's own rules too, plus their LINE particles and ICTS/IITS/IDTS child systems
        // (RetroParticleSystem.P1; the Prime 1 arena effects, Arenas/Prime1Fx.cs, 2026-11-16)
        public bool Prime1Rules;

        // every "<hex id>.part" in a folder (what Import/Effects/BodyFx.cs writes; mp3: Corruption's, HyperFx.cs)
        public static RetroParticles Load(string dir, bool mp3 = false, bool echoes = false)
        {
            var lib = new RetroParticles();
            if (!Directory.Exists(dir)) return lib;
            foreach (string f in Directory.GetFiles(dir, "*.part"))
            {
                if (UInt32.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out uint id))
                {
                    lib.Add(id, Part.Load(File.ReadAllBytes(f), mp3, echoes));
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
        // Corruption (Mp3Rules): PBDM's blend and the bloom it marks (RetroBlends.Mp3); Default: Additive says
        public RetroBlend Blend;
        public float Glow;
        public bool DepthWrite;
        public readonly RetroBlend Mode => Blend != RetroBlend.Default ? Blend : Additive ? RetroBlend.Add : RetroBlend.Alpha;
        // ORNT (Echoes effects): the sprite runs along Fore (world, its full length: the particle's direction times its
        // size; the texture's u), Width (ROTA, else 1) times Fore x the camera's forward across it (v); zero: a billboard
        public Vector3 Fore;
        public float Width;
        // LINE (Corruption, RetroRules): a strip from Position to LineEnd (world), LineWidthPx pixels wide on a 448-line
        // screen, the tile's (u0, v0) at the start and (u1, v1) at the end on both edges; zero: not a line
        public Vector3 LineEnd;
        public float LineWidthPx;
        // Corruption (Mp3Rules) ALSC (main.dol 0x80363e60: GXSetTevSwapMode(0, 0, ALSC + 1)): the texture's alpha read
        // from its red (1), green (2) or blue (3); 0: its own alpha
        public byte AlphaFrom;
        // Corruption SORT: drawn back to front among its run (same blend, texture and AlphaFrom)
        public bool Sorted;
    }

    // one model particle to draw (PMDL): its CMDL, placed by Transform (effect space = the model's own space -> world, row
    // vectors), times Color (PMCL; additive: the material times it, added). PMUS (Model 0): instead a square of Texture
    // (Uv: its tile, v 0 = the tile's first row), corners (+-0.5, 0, +-0.5) in that space, Color times the texture
    // (additive: src alpha + one, else alpha blended)
    public struct RetroModelParticle
    {
        public uint Model;
        public Matrix4 Transform;
        public Vector4 Color;
        public bool Additive;
        public uint Texture;
        public Vector4 Uv;
        public byte AlphaFrom; // as RetroBillboard's
    }

    public sealed class RetroParticleSystem
    {
        sealed class Particle
        {
            public Vector3 Pos, Vel;    // world (Follow: effect space)
            public Vector3 PrevPos;     // Pos before this frame's move (LINE)
            public int Age, Life;
            public readonly Dictionary<Part.Elem, float> Init = new(ReferenceEqualityComparer.Instance);
        }

        readonly RetroParticles _lib;
        readonly Part _part;
        readonly Random _rand;
        readonly List<Particle> _live = new();
        readonly Stack<Particle> _free = new(); // spent particles for the next ones (each held a dictionary: garbage per spawn)
        readonly List<RetroParticleSystem> _kids = new();
        readonly RetroParticleSystem? _parent;
        float _remainder;
        int _frame, _made;
        bool _emitting = true;

        public uint Id { get; }
        public RetroParticles Lib => _lib;
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
                int pslt = SysInt("PSLT", Int32.MaxValue);
                // Prime 1: the IDTS children start on frame PSLT
                if (P1 && _part["IDTS"] is Part.Elem idts && idts.Id != 0 && _frame <= pslt) return false;
                return _frame >= pslt;
            }
        }

        // one 60 Hz tick: PSTS scales the system's own frames a tick (CElementGen::InternalUpdate; its kids step on
        // these frames and scale by their own PSTS)
        public void Step()
        {
            if (_part["PSTS"] == null)
            {
                StepFrame();
                return;
            }
            _timeAcc += Math.Max(0, SysReal("PSTS", 1));
            while (_timeAcc > 0.9999f)
            {
                _timeAcc -= 1;
                StepFrame();
            }
        }

        float _timeAcc;

        void StepFrame()
        {
            foreach (Part.Spawn s in _part.Spawns)
            {
                if (s.Frame == _frame && _lib.Get(s.Part) is Part kid)
                {
                    _kids.Add(new RetroParticleSystem(_lib, kid, s.Part, Transform, Follow, _rand, this));
                }
            }
            if (P1) ChildSystems();
            foreach (RetroParticleSystem k in _kids) k.Transform = Transform;
            // age and move the live ones, then add this frame's new ones
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Particle p = _live[i];
                p.Age++;
                // Retro keeps a particle through its birth frame + LTME (removed once mEndFrame < mCurFrame), so a
                // MAXP 1 system with PSLT just past LTME doesn't get a second one in the freed slot
                if (p.Age > p.Life)
                {
                    _live.RemoveAt(i);
                    Free(p);
                    continue;
                }
                p.Vel += ModVelocity(p, out bool kill);
                if (kill)
                {
                    _live.RemoveAt(i);
                    Free(p);
                    continue;
                }
                p.PrevPos = p.Pos;
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

        // Prime 1's child systems (CElementGen's ICTS / IITS / IDTS): ICTS, NCSY of them on frame CSSD; IITS, one every
        // PISY frames from frame SISY while emitting; IDTS, NDSY of them as the emission ends (frame PSLT)
        void ChildSystems()
        {
            if (_part["ICTS"] is Part.Elem icts && _frame == SysInt("CSSD", 0)) AddKids(icts.Id, SysInt("NCSY", 1));
            int pslt = SysInt("PSLT", Int32.MaxValue);
            if (_part["IITS"] is Part.Elem iits && Emitting && _frame < pslt)
            {
                int sisy = SysInt("SISY", 0), pisy = Math.Max(1, SysInt("PISY", 1));
                if (_frame >= sisy && (_frame - sisy) % pisy == 0) AddKids(iits.Id, 1);
            }
            if (_part["IDTS"] is Part.Elem idts && _frame == pslt) AddKids(idts.Id, SysInt("NDSY", 1));
        }

        void AddKids(uint id, int n)
        {
            if (id == 0 || id == UInt32.MaxValue || _lib.Get(id) is not Part kid) return;
            for (int i = 0; i < Math.Min(n, 16); i++) _kids.Add(new RetroParticleSystem(_lib, kid, id, Transform, Follow, _rand, this));
        }

        void Emit()
        {
            Particle p = NewParticle();
            var c = new Ctx(p, _frame, 1);
            // Corruption keeps a 0 lifetime (drawn on its birth frame only)
            p.Life = Math.Max(Mp3Rules ? 0 : 1, EvalInt(_part["LTME"], c, 1));
            c.Life = p.Life;
            _made++;
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
                    {
                        Vector3 centre = EvalVec(em.Kid(0), c);
                        float radius = EvalReal(em.Kid(1), c, 0), speed = EvalReal(em.Kid(2), c, 0);
                        Vector3 dir = RandomDir();
                        pos = centre + dir * radius;
                        vel = dir * speed;
                        break;
                    }
                    case "ASPH":
                    {
                        // CParticleDataFactory reads origin, X bias, Y bias, X range, Y range, radius, speed (CVEAngleSphere):
                        // a direction X/Y degrees off +Z, each its bias plus a random share of its range (as RetroElectric)
                        Vector3 centre = EvalVec(em.Kid(0), c);
                        float xb = EvalReal(em.Kid(1), c, 0), yb = EvalReal(em.Kid(2), c, 0);
                        float xr = EvalReal(em.Kid(3), c, 0), yr = EvalReal(em.Kid(4), c, 0);
                        float radius = EvalReal(em.Kid(5), c, 0), speed = EvalReal(em.Kid(6), c, 0);
                        float ax = MathHelper.DegreesToRadians(xb + xr * 0.5f - Rnd() * xr);
                        float ay = MathHelper.DegreesToRadians(yb + yr * 0.5f - Rnd() * yr);
                        var dir = new Vector3(-MathF.Sin(ay) * MathF.Cos(ax), MathF.Sin(ax), MathF.Cos(ax) * MathF.Cos(ay));
                        pos = centre + dir * radius;
                        vel = dir * speed;
                        break;
                    }
                    case "ELPS":
                    {
                        // Echoes' ellipsoid: a point on the ellipsoid of these radii about the centre, sent out from it
                        Vector3 centre = EvalVec(em.Kid(0), c), radii = EvalVec(em.Kid(1), c);
                        float speed = EvalReal(em.Kid(3), c, 0);
                        Vector3 dir = RandomDir();
                        pos = centre + dir * radii;
                        Vector3 off = pos - centre;
                        vel = off.LengthSquared > 1e-12f ? off.Normalized() * speed : dir * speed;
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
            // as if it had moved here: a LINE shows its length from its first frame
            p.PrevPos = p.Pos - p.Vel;
            _live.Add(p);
        }

        // one particle now at a point (world; Follow: effect space), living the given frames, for an effect the game's code
        // places itself (Corruption's PhazonBallContactPoints has no rate of its own and takes its life from code)
        public void EmitAt(Vector3 at, int life)
        {
            if (_live.Count >= SysInt("MAXP", 256)) return;
            Particle p = NewParticle();
            p.Pos = p.PrevPos = at;
            p.Life = Math.Max(1, life);
            _live.Add(p);
        }

        Particle NewParticle() => _free.Count > 0 ? _free.Pop() : new Particle();

        void Free(Particle p)
        {
            p.Pos = p.Vel = p.PrevPos = Vector3.Zero;
            p.Age = p.Life = 0;
            p.Init.Clear();
            _free.Push(p);
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

        static readonly string[] AdvTags = { "ADV1", "ADV2", "ADV3", "ADV4", "ADV5", "ADV6", "ADV7", "ADV8" };

        static readonly string[] ModTags = { "PSVM", "VEL1", "VEL2", "VEL3", "VEL4" };

        // PSVM / VEL1-4: the constant, gravity and wind kinds, and Corruption's IMPL / SWRL / SPHV (the others are left
        // out); kill = an implosion took the particle
        Vector3 ModVelocity(Particle p, out bool kill)
        {
            kill = false;
            Vector3 d = Vector3.Zero;
            Ctx c = Of(p);
            Matrix4? inv = null;
            foreach (string tag in ModTags)
            {
                Part.Elem? m = _part[tag];
                if (m == null) continue;
                // CHAN (CMVETimeChain): the first until the switch frame, then the second
                for (int n = 0; Retro && m != null && m.Kind == "CHAN" && n < 8; n++) m = c.Frame < EvalInt(m.Kid(2), c, 0) ? m.Kid(0) : m.Kid(1);
                if (m == null) continue;
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
                    case "IMPL" or "SWRL" or "SPHV" when Mp3Rules:
                    {
                        // on the velocity so far (Retro runs VEL1-4 in turn on one velocity), in the system's own space
                        // (unscaled, as Retro's particle space: the effect's placement is applied when drawn)
                        inv ??= Follow ? Matrix4.Identity : Transform.Inverted();
                        Vector3 lp = Vector3.TransformPosition(p.Pos, inv.Value);
                        Vector3 lv = Vector3.TransformVector(p.Vel + d, inv.Value);
                        Vector3 nv = lv;
                        if (Mp3Mod(m, c, ref nv, lp)) kill = true;
                        v = nv - lv;
                        break;
                    }
                    default: continue;
                }
                d += Follow ? v : Vector3.TransformVector(v, Transform);
            }
            return d;
        }

        // Corruption's pulls (PrimeDecomp CModVectorElement: CMVEImplosion, CMVESwirl, CMVESphereVolume) on a velocity at a
        // position; true = the particle dies (inside an implosion's inner radius)
        bool Mp3Mod(Part.Elem m, Ctx c, ref Vector3 vel, Vector3 pos)
        {
            switch (m.Kind)
            {
                case "IMPL":
                {
                    // toward the point by a fixed amount within maxMag; inside minMag (when enabled) it dies
                    Vector3 dv = EvalVec(m.Kid(0), c) - pos;
                    float dist = dv.Length;
                    if (dist > EvalReal(m.Kid(2), c, 0)) return false;
                    if (m.Kid(4)?.B == true && dist < EvalReal(m.Kid(3), c, 0)) return true;
                    if (dist == 0) return false;
                    vel += EvalReal(m.Kid(1), c, 0) * (dv / dist);
                    return false;
                }
                case "SWRL":
                {
                    // round the helix axis (point, binormal): the velocity along the axis plus the tangential speed times
                    // the axis cross the way to it, blended in by the filter gain
                    Vector3 a = EvalVec(m.Kid(0), c), b = EvalVec(m.Kid(1), c);
                    Vector3 toOrigin = a - pos;
                    Vector3 toHelix = toOrigin - Vector3.Dot(toOrigin, b) * b;
                    float gain = EvalReal(m.Kid(2), c, 0), tangential = EvalReal(m.Kid(3), c, 0);
                    vel = (b * Vector3.Dot(b, vel) + Vector3.Cross(b, toHelix) * tangential) * gain + (1 - gain) * vel;
                    return false;
                }
                case "SPHV":
                {
                    // the inner modifier only inside the sphere (it can't kill: its result is dropped)
                    float r = EvalReal(m.Kid(1), c, 0);
                    if (m.Kid(2) is Part.Elem inner && (pos - EvalVec(m.Kid(0), c)).LengthSquared < r * r)
                    {
                        Mp3Mod(inner, c, ref vel, pos);
                    }
                    return false;
                }
            }
            return false;
        }

        // the billboards to draw this frame (children included)
        public void Collect(List<RetroBillboard> into)
        {
            Part.Elem? tex = _part["TEXR"];
            // RDOP (PrimeDecomp CElementGen::Render, on by default): off = no sprites, so a PMUS system draws only its
            // squares; LINE systems draw their lines either way. Corruption only
            bool noSprites = Mp3Rules && !_part.Flag("LINE") && _part["RDOP"] is Part.Elem rdop && !rdop.B;
            if (tex != null && tex.Id != 0 && _live.Count > 0 && !noSprites)
            {
                float scale = Transform.Row0.Xyz.Length;
                // Corruption: PBDM, a blend mode (0 alpha, else a glow: added)
                bool additive = _part.Flag("AAPH") || _part["PBDM"] is Part.Elem bm && bm.Kind == "CNST" && bm.I != 0;
                (RetroBlend blend, float glow) = Mp3Rules && _part["PBDM"] is Part.Elem bm3 && bm3.Kind == "CNST"
                    ? RetroBlends.Mp3(bm3.I) : (RetroBlend.Default, 0f);
                bool zbuf = _part.Flag("ZBUF");
                byte alphaFrom = AlphaFrom();
                bool sorted = Mp3Rules && _part.Flag("SORT");
                bool oriented = Retro && _part.Flag("ORNT");
                // LINE (PrimeDecomp CElementGen::RenderLines): from the particle to LENG times its last move ahead (FXLL: times
                // its direction), WIDT pixels wide; SIZE and ROTA unused. Corruption and Prime1Rules (Echoes keeps its sprites)
                bool line = (Mp3Rules || P1) && _part.Flag("LINE");
                Part.Elem? pofsE = _part["POFS"];
                FlipbookSetup(tex, out int tilesX, out int tiles, out Vector4 tileSize, out Vector2 stride, out int cycle, out bool loop);
                foreach (Particle p in _live)
                {
                    var c = Of(p);
                    float size = line ? 0 : EvalReal(_part["SIZE"], c, 0.1f);
                    if (!line && size <= 0.0005f) continue;
                    Vector4 col = EvalColor(_part["COLR"], c);
                    if (col.W <= 0.002f) continue;
                    Vector3 pos = Follow ? Vector3.TransformPosition(p.Pos, Transform) : p.Pos;
                    if (pofsE != null) pos += Vector3.TransformVector(EvalVec(pofsE, c), Transform);
                    if (line)
                    {
                        Vector3 d = p.Pos - p.PrevPos;
                        if (_part.Flag("FXLL") && d.LengthSquared > 1e-12f) d.Normalize();
                        d *= EvalReal(_part["LENG"], c, 1);
                        into.Add(new RetroBillboard
                        {
                            Position = pos,
                            LineEnd = pos + (Follow ? Vector3.TransformVector(d, Transform) : d),
                            LineWidthPx = MathF.Max(0.01f, EvalReal(_part["WIDT"], c, 1)),
                            Color = col,
                            Texture = tex.Id,
                            Uv = tiles > 0 ? Flipbook(p.Age, tilesX, tiles, tileSize, stride, Cycle(tex, c, cycle), loop)
                                : tex.Kind == "TEXP" ? AtlasFrame(tex, c) : default,
                            Additive = additive,
                            Blend = blend,
                            Glow = glow,
                            DepthWrite = zbuf,
                            AlphaFrom = alphaFrom,
                            Sorted = sorted,
                        });
                        continue;
                    }
                    // ORNT (PrimeDecomp CElementGen::RenderParticles): along the velocity, else the last move, else +Z
                    Vector3 fore = Vector3.Zero;
                    if (oriented)
                    {
                        Vector3 v = Follow ? Vector3.TransformVector(p.Vel, Transform) : p.Vel;
                        if (v.LengthSquared <= 1e-12f) v = Follow ? Vector3.TransformVector(Vector3.UnitZ, Transform) : Vector3.UnitZ;
                        fore = v.Normalized() * size * scale;
                    }
                    into.Add(new RetroBillboard
                    {
                        Fore = fore,
                        Width = oriented && _part["ROTA"] != null ? EvalReal(_part["ROTA"], c, 1) : 1,
                        Position = pos,
                        Size = size * scale,
                        Rotation = EvalReal(_part["ROTA"], c, 0),
                        Color = col,
                        Texture = tex.Id,
                        Uv = tiles > 0 ? Flipbook(p.Age, tilesX, tiles, tileSize, stride, Cycle(tex, c, cycle), loop)
                            : tex.Kind == "TEXP" ? AtlasFrame(tex, c) : default,
                        Additive = additive,
                        Blend = blend,
                        Glow = glow,
                        DepthWrite = zbuf,
                        AlphaFrom = alphaFrom,
                        Sorted = sorted,
                    });
                }
            }
            foreach (RetroParticleSystem k in _kids) k.Collect(into);
        }

        // ALSC (Corruption, main.dol 0x80363e60): 0-2 take the texture's alpha from its red, green or blue (swap table
        // ALSC + 1: RGBR, RGBG, RGBB); 3 or none, its own alpha. As RetroBillboard.AlphaFrom (1-3; 0: its own)
        byte AlphaFrom() => Mp3Rules && _part["ALSC"] is Part.Elem al && al.Kind == "CNST" && al.I is >= 0 and <= 2 ? (byte)(al.I + 1) : (byte)0;

        // the model particles (PMDL), as Metaforce's CElementGen::RenderModels: each at its position plus PMOP (in the
        // system's turn), turned by PMRT (degrees: Z, then Y, then X about the turned axes) and scaled by PMSC, in the
        // system's turn (PMOO: the model keeps the world's axes instead); coloured PMCL (else white); PMAB = additive. Not
        // lit (the game code's modelsUseLights is off by default); COLR and SIZE are the sprites'.
        // PMUS (Metaforce RenderModels' PMUS branch): no model, a textured square in the particle's XZ plane (corners
        // (+-0.5, 0, +-0.5): (0.5, 0.5) at the tile's (u1, v1)); every square shows the TEXR tile of the system's oldest
        // particle's age.
        public void CollectModels(List<RetroModelParticle> into)
        {
            Part.Elem? pmdl = _part["PMDL"];
            bool square = _part.Flag("PMUS");
            if ((square || pmdl != null && pmdl.Id is not (0 or UInt32.MaxValue)) && _live.Count > 0)
            {
                uint squareTex = 0;
                Vector4 squareUv = new(0, 0, 1, 1);
                Part.Elem? ownTile = null;
                if (square && _part["TEXR"] is Part.Elem tex && tex.Id is not (0 or UInt32.MaxValue))
                {
                    squareTex = tex.Id;
                    FlipbookSetup(tex, out int tilesX, out int tiles, out Vector4 tileSize, out Vector2 stride, out int cycle, out bool loop);
                    Particle first = _live[0];
                    if (tiles > 0) squareUv = Flipbook(first.Age, tilesX, tiles, tileSize, stride, Cycle(tex, Of(first), cycle), loop);
                    else if (tex.Kind == "TEXP") squareUv = AtlasFrame(tex, Of(first));
                    // Corruption (main.dol 0x8036203c): a square whose UV isn't constant draws its own particle's tile
                    // (kept beside its ADVs), not the first particle's
                    if (Mp3Rules && tex.Kind == "TEXP") ownTile = tex;
                }
                if (square && squareTex == 0) { foreach (RetroParticleSystem k in _kids) k.CollectModels(into); return; }
                // Corruption: MBDM, the models' blend mode (0 alpha, else added), as PBDM for the sprites
                bool additive = _part.Flag("PMAB") || Mp3Rules && _part["MBDM"] is Part.Elem mb && mb.Kind == "CNST" && mb.I != 0;
                bool worldAxes = _part.Flag("PMOO");
                Part.Elem? pmop = _part["PMOP"], pmrt = _part["PMRT"], pmsc = _part["PMSC"], pmcl = _part["PMCL"];
                // Corruption PMOV (main.dol 0x803628a0): the model looks along it (LookAt from 0 to it, up +Z: its +Y on
                // it), after PMRT; in the system's own space (a world velocity, not following, is taken back into it)
                Part.Elem? pmov = Mp3Rules ? _part["PMOV"] : null;
                Matrix4 unturn = pmov != null && !Follow ? Matrix4.Invert(Transform.ClearTranslation()) : Matrix4.Identity;
                byte alphaFrom = AlphaFrom();
                // the system's turn and scale (effect -> world, no move); PMOO: its scale only
                Matrix4 turn = Transform.ClearTranslation();
                if (worldAxes) turn = Matrix4.CreateScale(Transform.Row0.Xyz.Length);
                foreach (Particle p in _live)
                {
                    var c = Of(p);
                    Vector3 r = pmrt != null ? EvalVec(pmrt, c) : Vector3.Zero, sc = pmsc != null ? EvalVec(pmsc, c) : Vector3.One;
                    Matrix4 local = Matrix4.CreateScale(sc)
                        * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(r.X))
                        * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(r.Y))
                        * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(r.Z));
                    if (pmov != null)
                    {
                        Vector3 v = EvalVec(pmov, c);
                        if (!Follow) v = Vector3.TransformVector(v, unturn);
                        if (LookAlong(v) is Matrix4 look) local *= look;
                    }
                    local *= Matrix4.CreateTranslation(pmop != null ? EvalVec(pmop, c) : Vector3.Zero);
                    Vector3 at = Follow ? Vector3.TransformPosition(p.Pos, Transform) : p.Pos;
                    into.Add(new RetroModelParticle
                    {
                        Model = square ? 0 : pmdl!.Id,
                        Texture = squareTex,
                        Uv = ownTile != null ? AtlasFrame(ownTile, c) : squareUv,
                        Transform = local * turn * Matrix4.CreateTranslation(at),
                        Color = pmcl != null ? EvalColor(pmcl, c) : Vector4.One,
                        Additive = additive,
                        AlphaFrom = alphaFrom,
                    });
                }
            }
            foreach (RetroParticleSystem k in _kids) k.CollectModels(into);
        }

        // Retro's CTransform::LookAt (0 -> v, up +Z) as a turn (row vectors): +X right (v x up), +Y along v, +Z up; none
        // when v is (nearly) zero or along +Z
        static Matrix4? LookAlong(Vector3 v)
        {
            if (v.LengthSquared < 1e-12f) return null;
            Vector3 look = v.Normalized(), right = Vector3.Cross(look, Vector3.UnitZ);
            if (right.LengthSquared < 1e-8f) return null;
            right.Normalize();
            Vector3 up = Vector3.Cross(right, look);
            return new Matrix4(new Vector4(right, 0), new Vector4(look, 0), new Vector4(up, 0), Vector4.UnitW);
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

        // TEXP (Corruption): the texture cut into tiles (its two ints wide / high), the tile its real picks, counted from
        // the top-left along the row. Mp3Rules: the real spans all the tiles 0..1 (main.dol 0x803e0660: tile =
        // trunc(real * tiles), clamped), so the lightning's IRND(0,1) PAP1 picks any of its 16 bolts
        Vector4 AtlasFrame(Part.Elem tex, Ctx c)
        {
            if (_lib.TextureSize(tex.Id) is not (int w, int h) || w <= 0 || h <= 0) return default;
            int tw = Math.Max(1, EvalInt(tex.Kid(0), c, w)), th = Math.Max(1, EvalInt(tex.Kid(1), c, h));
            int cols = Math.Max(1, w / tw), rows = Math.Max(1, h / th);
            float pick = EvalReal(tex.Kid(2), c, 0);
            int tile = Math.Clamp((int)(Mp3Rules ? pick * cols * rows : pick), 0, cols * rows - 1);
            float u0 = tile % cols * tw / (float)w, v0 = tile / cols * th / (float)h;
            return new Vector4(u0, v0, u0 + tw / (float)w, v0 + th / (float)h);
        }

        // KEYF (Corruption): its keys looked up by the driver's value (the header's value at the last key)
        // (a vector's keys: `width` floats each, `comp` the one wanted)
        float KeyedBy(Part.Elem e, Ctx c, int width = 1, int comp = 0)
        {
            float[] k = e.Keys!;
            int n = k.Length / width;
            if (n == 0) return 0;
            if (Mp3Rules)
            {
                // Corruption's own lookup (main.dol: real 0x803b6f7c, int 0x80378054; the int one truncates): key =
                // trunc((v - start) * keys per unit), wrapped from the loop start past the end or held at the last; a
                // blend toward the next key only between the ends, by the driver distance past the key, clamped 0..1
                float d = EvalReal(e.Kid(0), c, 0) - e.KeyStart;
                int j = Math.Max(0, (int)(d * e.F));
                if (!e.Loop) j = Math.Min(j, n - 1);
                else if (j >= n) j = n - e.LoopStart > 0 ? (j - e.LoopStart) % (n - e.LoopStart) + e.LoopStart : n - 1;
                if (j <= 0 || j >= n - 1) return k[j * width + comp];
                float t = Math.Clamp(d - j / e.F, 0, 1);
                return (1 - t) * k[j * width + comp] + t * k[(j + 1) * width + comp];
            }
            float span = e.F > 1e-6f ? e.F : n - 1;
            float x = Math.Clamp(EvalReal(e.Kid(0), c, 0) / span, 0, 1) * (n - 1);
            int i = Math.Min((int)x, n - 1);
            return i >= n - 1 ? k[(n - 1) * width + comp] : Lerp(k[i * width + comp], k[(i + 1) * width + comp], x - i);
        }

        // the cycle's frames for one particle: Metaforce's GetValueUV evaluates it each time, at the particle's age and
        // lifetime (ILPT: a share of that lifetime; the impact flash's ATEX is ILPT(100), its whole 40-frame life)
        int Cycle(Part.Elem tex, Ctx c, int setup) => tex.Kind == "ATEX" && tex.Kid(4) is { Kind: not "CNST" } k ? Math.Max(1, EvalInt(k, c, 1)) : setup;

        static Vector4 Flipbook(int age, int tilesX, int tiles, Vector4 tileSize, Vector2 stride, int cycle, bool loop)
        {
            int f = loop ? age % cycle : Math.Min(age, cycle - 1);
            int tile = Math.Clamp(f * tiles / cycle, 0, tiles - 1);
            int x = tile % tilesX, y = (int)tileSize.W - 1 - tile / tilesX;
            float u0 = x * stride.X, v0 = y * stride.Y;
            return new Vector4(u0, v0, u0 + tileSize.X, v0 + tileSize.Y);
        }

        // ---- element evaluation ----

        // Frame: the particle's age (system-level values: the system's frame); Life: the particle's lifetime; Emitter:
        // the system's frame where a frame-keyed KEYE reads it (Echoes' effects; -1: Frame)
        // (a struct: one per particle per pass was most of the Odin's Mining Plaza effect garbage, 2026-11-01)
        struct Ctx
        {
            public readonly Particle? P;    // null: a system-level value
            public readonly int Frame;
            public int Life;
            public int Emitter;
            public Ctx(Particle? p, int frame, int life) { P = p; Frame = frame; Life = life; Emitter = -1; }
        }

        // a particle's context. Retro's frame-keyed KEYE (CREKeyframeEmitter, not percent) reads the emitter's own time
        // (CParticleGlobals m_EmitterTime: the generator's frame), whatever the particle's age; done for Echoes' effects
        // (the Shooting Gallery turret's: its lift rings stretch once as the lift starts, not each time a ring is
        // replaced) and the Hyper Beam's (Retro), the other Prime 1 / Corruption ones keep the particle's age as before
        Ctx Of(Particle p) => new(p, p.Age, p.Life) { Emitter = Retro ? _frame : -1 };

        // Retro's own rules (ORNT, frame-keyed KEYE, CHAN, ANGC's +Z cone, CIRC): Echoes' effects, and Corruption's where
        // the library asks for them (RetroParticles.RetroRules: the Hyper Beam's); the others keep what they were tuned
        // with. Mp3Rules alone (LINE, MBDM): only those Corruption ones
        bool Retro => _part.Echoes || Mp3Rules || P1;
        bool Mp3Rules => _part.Mp3 && _lib.RetroRules;
        // Prime 1 effects in a library that asks for Retro's rules (RetroParticles.Prime1Rules)
        bool P1 => !_part.Echoes && !_part.Mp3 && _lib.Prime1Rules;

        float Rnd() => (float)_rand.NextDouble();

        int SysInt(string tag, int dflt) => EvalInt(_part[tag], new Ctx(null, _frame, 1), dflt);
        float SysReal(string tag, float dflt) => EvalReal(_part[tag], new Ctx(null, _frame, 1), dflt);

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // IRND: once per particle (system-level: each time): the particle's kept value, else the caller works one out and
        // keeps it (no Func: a lambda capturing the Ctx parameter allocated a closure on every EvalReal call)
        static bool HasInitial(Part.Elem e, Ctx c, out float v)
        {
            v = 0;
            return c.P != null && c.P.Init.TryGetValue(e, out v);
        }

        static float KeepInitial(Part.Elem e, Ctx c, float v)
        {
            if (c.P != null) c.P.Init[e] = v;
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
            int f = c.Emitter >= 0 ? c.Emitter : c.Frame;
            if (e.Loop && f >= e.LoopEnd && e.LoopEnd > e.LoopStart) f = (f - e.LoopStart) % (e.LoopEnd - e.LoopStart) + e.LoopStart;
            f = Math.Clamp(f, 0, n - 1);
            return k[f * width + lane];
        }

        // PULS(a, b, A, B), Retro's pulse elements (PrimeDecomp CIntElement / CRealElement / CVectorElement /
        // CColorElement.cpp): a cycle of a + b + 1 frames, A for its first a + 1 and B for the rest; the real one A for a
        // and B for b + 1 (it tests >= where the others test >); b < 1: A always
        bool PulseA(Part.Elem e, Ctx c, bool real = false)
        {
            int a = EvalInt(e.Kid(0), c, 0), b = EvalInt(e.Kid(1), c, 0);
            if (b < 1) return true;
            int cv = a + b + 1;
            if (cv <= 0) cv = 1;
            int at = c.Frame % cv;
            return real ? at < a : at <= a;
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
                    return (int)(HasInitial(e, c, out float iv) ? iv : KeepInitial(e, c, a + (int)(Rnd() * (b - a + 1))));
                }
                case "ADD_": return EvalInt(e.Kid(0), c, 0) + EvalInt(e.Kid(1), c, 0);
                case "SUB_": return EvalInt(e.Kid(0), c, 0) - EvalInt(e.Kid(1), c, 0);
                case "MULT": return EvalInt(e.Kid(0), c, 0) * EvalInt(e.Kid(1), c, 0);
                case "PULS": return PulseA(e, c) ? EvalInt(e.Kid(2), c, 0) : EvalInt(e.Kid(3), c, 0);
                case "CLMP": return Math.Clamp(EvalInt(e.Kid(2), c, 0), EvalInt(e.Kid(0), c, 0), EvalInt(e.Kid(1), c, 0));
                case "TSCL": return (int)(EvalReal(e.Kid(0), c, 0) * c.Frame);
                case "RTOI": return (int)(EvalReal(e.Kid(0), c, 0) * EvalReal(e.Kid(1), c, 1));
                case "ILPT": return (int)(Math.Max(0, EvalInt(e.Kid(0), c, 0)) / 100f * c.Life + 0.5f);
                case "IMPL": return EvalInt(e.Kid(0), c, 0);
                case "KEYF": return (int)KeyedBy(e, c);
                // CRETimeChain's kinds: the first until the switch frame, then the second (Retro: the other effects were
                // tuned without it)
                case "CHAN" when Retro: return c.Frame < EvalInt(e.Kid(2), c, 0) ? EvalInt(e.Kid(0), c, 0) : EvalInt(e.Kid(1), c, 0);
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
                    return HasInitial(e, c, out float iv) ? iv : KeepInitial(e, c, Lerp(a, b, Rnd()));
                }
                case "LFTW": case "ISWT":
                    return Lerp(EvalReal(e.Kid(0), c, 0), EvalReal(e.Kid(1), c, 0), Math.Min(1f, c.Frame / (float)Math.Max(1, c.Life)));
                case "RLPT": return Math.Max(0, EvalReal(e.Kid(0), c, 0)) / 100f * c.Life;
                case "SCAL": return EvalReal(e.Kid(0), c, 0) * c.Frame;
                case "PULS": return PulseA(e, c, real: true) ? EvalReal(e.Kid(2), c, 0) : EvalReal(e.Kid(3), c, 0);
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
                // Corruption: the system's external variable n (set only by scripts; none here: 0)
                case "CEXT": return Mp3Rules ? 0 : EvalInt(e.Kid(0), c, 0);
                case "PSLL": case "PRLW": return c.Frame;
                case "CHAN" when Retro: return c.Frame < EvalInt(e.Kid(2), c, 0) ? EvalReal(e.Kid(0), c, 0) : EvalReal(e.Kid(1), c, 0);
                // Corruption
                case "KEYF": return KeyedBy(e, c);
                case "KPIN": return HasInitial(e, c, out float kv) ? kv : KeepInitial(e, c, EvalReal(e.Kid(0), c, 0)); // its value at birth
                // Corruption: the particles this system has made so far (CElementGen +0x308; the one being made isn't counted)
                case "GTCP": return Mp3Rules ? _made : LiveParticles;
                // a particle's advanced value n (ADVn, kept per particle: its IRNDs once at birth)
                case "PAP1": case "PAP2": case "PAP3": case "PAP4": case "PAP5": case "PAP6": case "PAP7": case "PAP8":
                    return c.P != null && _part[AdvTags[e.Kind[3] - '1']] is Part.Elem adv ? EvalReal(adv, c, 0) : 0;
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
                case "KEYF": return new Vector3(KeyedBy(e, c, 3, 0), KeyedBy(e, c, 3, 1), KeyedBy(e, c, 3, 2));
                case "ADD_": return EvalVec(e.Kid(0), c) + EvalVec(e.Kid(1), c);
                case "SUB_": return EvalVec(e.Kid(0), c) - EvalVec(e.Kid(1), c);
                case "MULT": return EvalVec(e.Kid(0), c) * EvalVec(e.Kid(1), c);
                case "RTOV": return new Vector3(EvalReal(e.Kid(0), c, 0));
                case "CHAN" when Retro: return c.Frame < EvalInt(e.Kid(2), c, 0) ? EvalVec(e.Kid(0), c) : EvalVec(e.Kid(1), c);
                case "PULS": return PulseA(e, c) ? EvalVec(e.Kid(2), c) : EvalVec(e.Kid(3), c);
                case "ANGC":
                {
                    // RotateZ(yBase + yRange * (r - 0.5)) * RotateX(xBase + xRange * (r - 0.5)) * (0, mag, 0)
                    float xb = EvalReal(e.Kid(0), c, 0), yb = EvalReal(e.Kid(1), c, 0);
                    float xr = EvalReal(e.Kid(2), c, 0), yr = EvalReal(e.Kid(3), c, 0), mag = EvalReal(e.Kid(4), c, 0);
                    float ax = MathHelper.DegreesToRadians(xb + xr * (Rnd() - 0.5f));
                    float az = MathHelper.DegreesToRadians(yb + yr * (Rnd() - 0.5f));
                    // Retro: its own cone about +Z (PrimeDecomp CVEAngleCone: (-sin y cos x, sin x, cos x cos y)); the
                    // others keep the +Y cone they were tuned with
                    if (Retro) return new Vector3(-MathF.Sin(az) * MathF.Cos(ax), MathF.Sin(ax), MathF.Cos(ax) * MathF.Cos(az)) * mag;
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
                case "PVEL" when Mp3Rules: return c.P?.Vel ?? Vector3.Zero; // the particle's velocity
                case "RNDV": return RandomDir() * EvalReal(e.Kid(0), c, 0); // Corruption: a random direction this long
                case "CIRC" when Retro:
                {
                    // CVECircle: a point on a ring about the centre, in the plane across the normal, at the angle constant +
                    // linear x frame (degrees) and the radius (Shooting Gallery's column bolts: 2.5 up, 70 degrees on per
                    // frame, RAND(0, 1.5) out; unread, every bolt stood on the column's axis at its foot)
                    Vector3 centre = EvalVec(e.Kid(0), c), n = EvalVec(e.Kid(1), c);
                    n = n.LengthSquared > 1e-12f ? n.Normalized() : Vector3.UnitZ;
                    float deg = EvalReal(e.Kid(2), c, 0) + EvalReal(e.Kid(3), c, 0) * c.Frame, r = EvalReal(e.Kid(4), c, 0);
                    Vector3 u = Vector3.Cross(n, MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY).Normalized(), v = Vector3.Cross(n, u);
                    float a = MathHelper.DegreesToRadians(deg);
                    return centre + (u * MathF.Cos(a) + v * MathF.Sin(a)) * r;
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
                // Corruption
                case "CFDL": return Vector4.Lerp(EvalColor(e.Kid(0), c), EvalColor(e.Kid(1), c), Math.Clamp(c.Frame / (float)Math.Max(1, c.Life), 0, 1));
                case "MULT": return EvalColor(e.Kid(0), c) * EvalColor(e.Kid(1), c);
                case "ADD_": return EvalColor(e.Kid(0), c) + EvalColor(e.Kid(1), c);
                case "SUB_": return EvalColor(e.Kid(0), c) - EvalColor(e.Kid(1), c);
                case "VRTC": return new Vector4(EvalVec(e.Kid(0), c), EvalReal(e.Kid(1), c, 1));
                case "MDAO": { Vector4 m = EvalColor(e.Kid(0), c); m.W *= EvalReal(e.Kid(1), c, 1); return m; }
                case "KPIN" when e.Kids.Length == 4:   // Echoes: the colour at birth, kept per particle
                {
                    if (HasInitial(e, c, out float r) && HasInitial(e.Kid(1)!, c, out float g) && HasInitial(e.Kid(2)!, c, out float b) && HasInitial(e.Kid(3)!, c, out float a)) return new Vector4(r, g, b, a);
                    Vector4 v = EvalColor(e.Kid(0), c);
                    KeepInitial(e, c, v.X); KeepInitial(e.Kid(1)!, c, v.Y); KeepInitial(e.Kid(2)!, c, v.Z); KeepInitial(e.Kid(3)!, c, v.W);
                    return v;
                }
            }
            return Vector4.One;
        }
    }
}
