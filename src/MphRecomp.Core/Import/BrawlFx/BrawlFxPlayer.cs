using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using MphRecomp.Assets;
using MphRecomp.Import.Brawl;

namespace MphRecomp.Import.BrawlFx
{
    // Our player for the Brawl effects the importer keeps (BrawlFxImportKind: brawl/<item>/ef_<fighter>.pac + common/*.png),
    // docs/BRAWL_PARALYZER.md B4. It runs the effect data the clean-room readers give (Import/Brawl: BrawlEffects,
    // BrawlEffectList, BrawlMesh, BrawlMeshAnimation) and hands world-space triangles to the app's renderer. This is our
    // reading of the data (the middle path, owner 2026-10-12: "whatever path gets us to a convincing same look"), checked
    // by eye against Brawl side by side, not a copy of Brawl's code:
    //   emitter    shape = emission flags' low byte (0 disc, 7 cylinder, 8 sphere, 9 point), shape params (x radius, fill %,
    //              start angle, sweep, y radius, z radius); emits EmitCount every EmitInterval frames (0 = each frame) for
    //              EmitterLife frames (0xFFFF = until stopped), EmitSubdivision times along its path per frame
    //   particle   life (+-random), speed from the centre / along the direction (Euler angles on +Y, spread) / random,
    //              +-random; follows its emitter when ParticleFollow > 0; billboard (shape 3) or a quad stretched along
    //              its velocity (shape 4)
    //   tracks     linear between keys over the particle's age (the emitter's for emitter tracks); kinds 0/1/2 = primary
    //              RGB / primary alpha / secondary RGB, 10 = random size (range = the largest), 11 = scale, 12 = random
    //              spin, 14 = texture (flipbook / pick), 26 = child effects (key value +8 = the name's number), 33 = drag
    //              (velocity x value per frame), 64 = emitter shape params
    //   colour     the particle TEV every Brawl effect here uses: rgb = lerp(secondary, primary, texture), alpha = primary
    //              alpha x texture alpha; blend dst 1 = additive, else alpha
    //   meshes     the effect list's model file per effect (bullets), bones posed by their CHR0, facing mode 6 = turned
    //              about its own axis to the camera; materials (Assets/Mdl0Gx) folded to the same lerp form
    public sealed record BrawlFxTexture(string Name, int Width, int Height, byte[]? Rgba, string? PngPath, bool IntensityAlpha);

    public sealed class BrawlFxModel
    {
        public BrawlMesh Mesh { get; init; } = null!;
        public BrawlMeshAnimation? Animation { get; init; }
        public Dictionary<string, GxMaterial> Materials { get; init; } = new();
    }

    // one vertex of the app's draw: world position, texture coordinate, primary colour (rgba), secondary colour (rgb)
    public struct BrawlFxVertex
    {
        public Vector3 Pos;
        public Vector2 Uv;
        public Vector4 Primary;
        public Vector3 Secondary;
    }

    // triangles sharing a texture (null = plain white) and a blend
    public sealed class BrawlFxDraw
    {
        public string? Texture;
        public bool Additive;
        public readonly List<BrawlFxVertex> Verts = new();
    }

    public sealed class BrawlFxLibrary
    {
        public BrawlEffectFile Effects { get; }
        public BrawlEffectList? List { get; }
        public IReadOnlyList<BrawlFxModel> Models { get; }
        public IReadOnlyDictionary<string, BrawlFxTexture> Textures { get; }

        BrawlFxLibrary(BrawlEffectFile effects, BrawlEffectList? list, List<BrawlFxModel> models, Dictionary<string, BrawlFxTexture> textures)
        {
            Effects = effects;
            List = list;
            Models = models;
            Textures = textures;
        }

        // the model an effect draws (the effect list's model file), or null
        public BrawlFxModel? ModelOf(string effect)
        {
            int n = List?.Find(effect)?.ModelFile ?? -1;
            return n >= 0 && n < Models.Count ? Models[n] : null;
        }

        // <dir> = brawl/<item>: the fighter's .pac (its effects, models and own textures) and common/<name>.<FMT>.png
        public static BrawlFxLibrary Load(string dir)
        {
            string pac = Directory.GetFiles(dir, "*.pac").Single();
            BrawlArchive archive = BrawlArchive.Read(File.ReadAllBytes(pac));
            BrawlEffectFile effects = BrawlEffects.Read(archive.DataOf(archive.Entries.First(e => e.Magic == "REFF")));
            BrawlArchiveEntry? efls = archive.Entries.FirstOrDefault(e => e.Magic == "EFLS");
            BrawlEffectList? list = efls == null ? null : BrawlEffectList.Read(archive.DataOf(efls));
            var textures = new Dictionary<string, BrawlFxTexture>(StringComparer.Ordinal);
            void Add(BrawlTexture t) => textures[t.Name] = new BrawlFxTexture(t.Name, t.Width, t.Height, t.Rgba, null,
                t.Format is BrawlPixels.I4 or BrawlPixels.I8);
            var models = new List<BrawlFxModel>();
            foreach (BrawlArchiveEntry e in archive.Entries)
            {
                if (e.Magic == "REFT")
                {
                    foreach (BrawlTexture t in BrawlTextures.ReadParticleTextures(archive.DataOf(e)).Textures) Add(t);
                    continue;
                }
                if (e.Magic != "bres") continue;
                BrawlResFile res = BrawlResFile.Read(archive.DataOf(e));
                if (res.Folders.Any(f => f.Name == BrawlResFile.Textures))
                {
                    foreach (BrawlTexture t in BrawlTextures.ReadResources(res, out _)) Add(t);
                }
                if (!res.Folders.Any(f => f.Name == BrawlResFile.Models)) continue;
                BrawlMesh mesh = BrawlMesh.ReadAll(res)[0];
                BrawlMeshAnimation? anim = res.Folders.Any(f => f.Name == BrawlResFile.MeshAnimations)
                    ? BrawlMeshAnimation.ReadAll(res).FirstOrDefault() : null;
                // the materials' GX programs: Mdl0Gx reads a file (the importer writes models/<n>.brres)
                string file = Path.Combine(dir, "models", $"{models.Count}.brres");
                if (!File.Exists(file))
                {
                    file = Path.Combine(Path.GetTempPath(), $"brawlfx_{Path.GetFileNameWithoutExtension(pac)}_{models.Count}.brres");
                    File.WriteAllBytes(file, archive.DataOf(e).ToArray());
                }
                models.Add(new BrawlFxModel
                {
                    Mesh = mesh,
                    Animation = anim,
                    Materials = Mdl0Gx.Read(file).GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First()),
                });
            }
            string common = Path.Combine(dir, "common");
            if (Directory.Exists(common))
            {
                foreach (string png in Directory.GetFiles(common, "*.png"))
                {
                    // <name>.<FMT>.png
                    string stem = Path.GetFileNameWithoutExtension(png);
                    int dot = stem.LastIndexOf('.');
                    if (dot <= 0) continue;
                    string name = stem[..dot], fmt = stem[(dot + 1)..];
                    textures[name] = new BrawlFxTexture(name, 0, 0, null, png, fmt is "I4" or "I8");
                }
            }
            return new BrawlFxLibrary(effects, list, models, textures);
        }
    }

    // One running effect (its root emitter and every child effect it spawns), in effect space -> world by Transform
    // (rotation x scale + translation). Step = one 60 Hz frame. Stop: emitters stop, particles run out (Done).
    public sealed class BrawlFxSystem
    {
        sealed class Emitter
        {
            public BrawlEffect Fx = null!;
            public int Age;
            public Emitter? ParentEmitter;     // the emitter whose particle spawned this one (null = the root)
            public Vector3 Offset;             // where it was spawned: parent emitter space (following) or world
            public bool Follows;               // rides its parent emitter (the parent effect's ChildEmitterFollow)
            public Matrix4x4 World;            // this frame's emitter space -> world
            public Vector3 LastOrigin;
            public bool HasLast;
            public float Carry;                // emission fraction carried over
            public bool Dead;
            public Particle? Source;           // the particle whose child effect this is
            public int Live;                   // its particles alive
        }

        sealed class Particle
        {
            public Emitter Em = null!;
            public int Age, Life;
            public Vector3 Pos, Vel;           // emitter space when Follows, else world
            public bool Follows;
            public Vector2 Size;
            public float Spin;
            public float Drag = 1;
            public Vector3 LastWorld;
            public bool Gone;
        }

        readonly BrawlFxLibrary _lib;
        readonly List<Emitter> _emitters = new();
        readonly List<Particle> _particles = new();
        readonly Random _rng;
        public Matrix4x4 Transform;
        public bool Stopped { get; private set; }
        public bool Done => Stopped && _particles.Count == 0 || _emitters.Count == 0 && _particles.Count == 0;
        // child effects (emitter life 0xFFFF) outlive the particle that made them, until Stop (a bullet's trail riding
        // the shot); else they end with it
        public bool KeepChildren;
        // every particle and child effect rides its emitter (moved with Transform), whatever the effect's follow flags say:
        // a muzzle flash staying on a moving gun (the Paralyzer's, owner 10-15)
        public bool FollowAll;
        public int Age { get; private set; }

        public BrawlFxSystem(BrawlFxLibrary lib, string effect, Matrix4x4 transform, int seed)
        {
            _lib = lib;
            _rng = new Random(seed);
            Transform = transform;
            if (lib.Effects.Find(effect) is BrawlEffect fx) _emitters.Add(new Emitter { Fx = fx, World = EmitterWorld(fx, transform) });
        }

        public void Stop() => Stopped = true;

        static Matrix4x4 Srt(Vector3 s, Vector3 r, Vector3 t) =>
            Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationZ(r.Z)
            * Matrix4x4.CreateTranslation(t);

        static Matrix4x4 EmitterWorld(BrawlEffect fx, Matrix4x4 origin) =>
            Srt(fx.Emitter.EmitterScale, fx.Emitter.EmitterRotation, fx.Emitter.EmitterTranslation) * origin;

        // the frame a child effect is placed in: the root transform's rotation/scale at a world point
        Matrix4x4 At(Vector3 world)
        {
            Matrix4x4 m = Transform;
            m.Translation = world;
            return m;
        }

        public void Step()
        {
            Age++;
            // emitters: place, then emit
            for (int i = 0; i < _emitters.Count; i++)
            {
                Emitter em = _emitters[i];
                Vector3 origin = em.ParentEmitter != null && em.Follows ? Vector3.Transform(em.Offset, em.ParentEmitter.World)
                    : em.ParentEmitter != null ? em.Offset : Transform.Translation;
                em.World = EmitterWorld(em.Fx, em.ParentEmitter == null ? Transform : At(origin));
                if (!Stopped && !em.Dead) Emit(em);
                em.Age++;
                BrawlEmitter e = em.Fx.Emitter;
                if (e.EmitterLife != 0xFFFF && em.Age >= e.EmitStartFrame + e.EmitterLife) em.Dead = true;
            }
            // particles: age, move, spawn children
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                Particle p = _particles[i];
                p.Age++;
                if (p.Age >= p.Life)
                {
                    p.Gone = true;
                    p.Em.Live--;
                    _particles.RemoveAt(i);
                    continue;
                }
                p.Vel *= p.Drag;
                p.Pos += p.Vel;
                Children(p);
            }
            foreach (Emitter em in _emitters)
            {
                if (!KeepChildren && em.Source is { Gone: true }) em.Dead = true;
            }
            _emitters.RemoveAll(em => em.Dead && em.Live == 0 && !_emitters.Any(c => c.ParentEmitter == em));
        }

        void Emit(Emitter em)
        {
            BrawlEmitter e = em.Fx.Emitter;
            int t = em.Age - e.EmitStartFrame;
            if (t < 0) return;
            if (e.EmitInterval > 0 && t % e.EmitInterval != 0) return;
            Vector3 origin = em.World.Translation;
            int div = Math.Max(1, (int)e.EmitSubdivision);
            Vector3 from = em.HasLast ? em.LastOrigin : origin;
            em.LastOrigin = origin;
            em.HasLast = true;
            float[] shape = EmitterValue(em, 64) ?? e.ShapeParams;
            for (int d = 0; d < div; d++)
            {
                float count = e.EmitCount * (1 - (float)_rng.NextDouble() * e.EmitCountRandom / 100f) + em.Carry;
                int n = (int)count;
                em.Carry = count - n;
                // the emitter's frame slid back along its path: the trail between frames filled
                Matrix4x4 world = em.World;
                if (div > 1) world.Translation = Vector3.Lerp(from, origin, (d + 1) / (float)div);
                for (int k = 0; k < n && _particles.Count < 2000; k++) Spawn(em, world, shape);
            }
        }

        void Spawn(Emitter em, Matrix4x4 world, float[] shape)
        {
            BrawlEmitter e = em.Fx.Emitter;
            BrawlParticle pd = em.Fx.Particle;
            float R() => (float)_rng.NextDouble();
            // where, in emitter space
            float fill = shape.Length > 1 ? Math.Clamp(shape[1] / 100f, 0, 1) : 1;
            float radius = 1 - fill * R();
            float angle = (shape.Length > 3 ? shape[2] + shape[3] * R() : 0);
            Vector3 pos;
            switch (e.EmissionFlags & 0xFF)
            {
                case 0: // disc in the XY plane
                    pos = new Vector3(MathF.Cos(angle) * shape[0] * radius, MathF.Sin(angle) * shape[4] * radius, 0);
                    break;
                case 7: // cylinder about Y
                    pos = new Vector3(MathF.Cos(angle) * shape[0] * radius, (R() - 0.5f) * shape[4], MathF.Sin(angle) * shape[5] * radius);
                    break;
                case 8: // sphere (ellipsoid), the sweep about Y
                {
                    float y = R() * 2 - 1, ring = MathF.Sqrt(1 - y * y);
                    pos = new Vector3(MathF.Cos(angle) * ring * shape[0], y * shape[4], MathF.Sin(angle) * ring * shape[5]) * radius;
                    break;
                }
                default: // point
                    pos = Vector3.Zero;
                    break;
            }
            // how fast, in emitter space
            Vector3 vel = Vector3.Zero;
            if (e.SpeedFromCentre != 0)
            {
                Vector3 outward = pos.LengthSquared() > 1e-8f ? Vector3.Normalize(pos) : RandomUnit();
                vel += outward * e.SpeedFromCentre;
            }
            if (e.SpeedFromYAxis != 0)
            {
                var flat = new Vector3(pos.X, 0, pos.Z);
                vel += (flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : RandomUnit()) * e.SpeedFromYAxis;
            }
            if (e.SpeedRandomDirection != 0) vel += RandomUnit() * e.SpeedRandomDirection;
            if (e.SpeedAlongDirection != 0)
            {
                Matrix4x4 rot = Matrix4x4.CreateRotationX(e.Direction.X) * Matrix4x4.CreateRotationY(e.Direction.Y)
                    * Matrix4x4.CreateRotationZ(e.Direction.Z);
                Vector3 dir = Vector3.TransformNormal(Vector3.UnitY, rot);
                if (e.DirectionSpread > 0) dir = Spread(dir, e.DirectionSpread);
                vel += dir * e.SpeedAlongDirection;
            }
            vel *= 1 - R() * e.InitialSpeedRandom / 100f;
            var p = new Particle
            {
                Em = em,
                Life = Math.Max(1, (int)MathF.Round(e.ParticleLife * (1 - R() * e.ParticleLifeRandom / 100f))),
                Follows = FollowAll || e.ParticleFollow > 0,
                Size = pd.Size,
            };
            // random size (kind 10): Size .. the range's largest; random spin (kind 12): 0 .. the range's z
            foreach (BrawlEffectTrack t in em.Fx.ParticleTracks)
            {
                if (t.Kind == 10 && RangeFirsts(t) is float[] big && big.Length >= 2)
                {
                    float r = R();
                    p.Size = new Vector2(pd.Size.X + (big[0] - pd.Size.X) * r, pd.Size.Y + (big[1] - pd.Size.Y) * r);
                }
                else if (t.Kind == 12 && RangeFirsts(t) is float[] spin && spin.Length >= 3) p.Spin = spin[2] * R();
                else if (t.Kind == 33 && t.Keys.Count > 0 && t.Keys[0].Floats is float[] drag) p.Drag = drag[0];
            }
            if (p.Follows)
            {
                p.Pos = pos;
                p.Vel = vel;
            }
            else
            {
                p.Pos = Vector3.Transform(pos, world);
                p.Vel = Vector3.TransformNormal(vel, world);
            }
            p.LastWorld = World(p);
            em.Live++;
            _particles.Add(p);
            Children(p); // age-0 child keys
        }

        Vector3 World(Particle p) => p.Follows ? Vector3.Transform(p.Pos, p.Em.World) : p.Pos;

        // child effects keyed at this particle's age (kind 26)
        void Children(Particle p)
        {
            foreach (BrawlEffectTrack t in p.Em.Fx.ParticleTracks)
            {
                if (t.Kind != 26 || t.Names.Count == 0) continue;
                foreach (BrawlTrackKey k in t.Keys)
                {
                    if (k.Frame != p.Age || k.RawValue.Length < 10) continue;
                    int index = BinaryPrimitives.ReadUInt16BigEndian(k.RawValue.AsSpan(8));
                    if (index >= t.Names.Count || _lib.Effects.Find(t.Names[index]) is not BrawlEffect child) continue;
                    bool follow = FollowAll || p.Em.Fx.Emitter.ChildEmitterFollow > 0;
                    Vector3 at = World(p);
                    var em = new Emitter
                    {
                        Fx = child,
                        ParentEmitter = p.Em,
                        Source = p,
                        Follows = follow,
                        Offset = follow ? (Matrix4x4.Invert(p.Em.World, out Matrix4x4 inv) ? Vector3.Transform(at, inv) : Vector3.Zero) : at,
                    };
                    em.World = EmitterWorld(child, At(at));
                    if (_emitters.Count < 200) _emitters.Add(em);
                }
            }
        }

        Vector3 RandomUnit()
        {
            float z = (float)_rng.NextDouble() * 2 - 1, a = (float)_rng.NextDouble() * MathF.Tau, r = MathF.Sqrt(1 - z * z);
            return new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, z);
        }

        Vector3 Spread(Vector3 dir, float spread)
        {
            Vector3 side = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            Vector3 up = Vector3.Cross(dir, side);
            float tilt = (float)_rng.NextDouble() * spread, turn = (float)_rng.NextDouble() * MathF.Tau;
            return Vector3.Normalize(dir * MathF.Cos(tilt) + (side * MathF.Cos(turn) + up * MathF.Sin(turn)) * MathF.Sin(tilt));
        }

        // a range part's first number per component (+4: count, pad; then 8 bytes per component)
        static float[]? RangeFirsts(BrawlEffectTrack t)
        {
            byte[] r = t.RangesPart;
            if (r.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(r) == 0) return null;
            var list = new List<float>();
            for (int o = 4; o + 4 <= r.Length; o += 8) list.Add(BinaryPrimitives.ReadSingleBigEndian(r.AsSpan(o)));
            return list.ToArray();
        }

        // a float track's value at a frame (linear between keys), or null without one of this kind
        static float[]? FloatsAt(IReadOnlyList<BrawlEffectTrack> tracks, int kind, float frame, int life = 0)
        {
            foreach (BrawlEffectTrack t in tracks)
            {
                if (t.Kind != kind || t.Keys.Count == 0 || t.Keys[0].Floats == null) continue;
                (BrawlTrackKey a, BrawlTrackKey b, float f) = Between(t, Fit(t, frame, life));
                float[] x = a.Floats!, y = b.Floats ?? x;
                var v = new float[x.Length];
                for (int i = 0; i < x.Length; i++) v[i] = x[i] + ((i < y.Length ? y[i] : x[i]) - x[i]) * f;
                return v;
            }
            return null;
        }

        // a byte track's value (colour RGB / alpha) at a frame, linear between keys
        static float[]? BytesAt(IReadOnlyList<BrawlEffectTrack> tracks, int kind, float frame, int count, int life = 0)
        {
            foreach (BrawlEffectTrack t in tracks)
            {
                if (t.Kind != kind || t.Keys.Count == 0) continue;
                (BrawlTrackKey a, BrawlTrackKey b, float f) = Between(t, Fit(t, frame, life));
                var v = new float[count];
                for (int i = 0; i < count; i++)
                {
                    float x = i < a.RawValue.Length ? a.RawValue[i] : 0, y = i < b.RawValue.Length ? b.RawValue[i] : x;
                    v[i] = (x + (y - x) * f) / 255f;
                }
                return v;
            }
            return null;
        }

        // fit to life: the track's frames spread over the particle's life
        static float Fit(BrawlEffectTrack t, float frame, int life) =>
            t.FitToLife && life > 1 && t.FrameCount > 1 ? frame / (life - 1) * (t.FrameCount - 1) : frame;

        static (BrawlTrackKey A, BrawlTrackKey B, float F) Between(BrawlEffectTrack t, float frame)
        {
            IReadOnlyList<BrawlTrackKey> k = t.Keys;
            if (frame <= k[0].Frame) return (k[0], k[0], 0);
            for (int i = 1; i < k.Count; i++)
            {
                if (frame <= k[i].Frame)
                {
                    float span = k[i].Frame - k[i - 1].Frame;
                    return (k[i - 1], k[i], span <= 0 ? 1 : (frame - k[i - 1].Frame) / span);
                }
            }
            return (k[^1], k[^1], 0);
        }

        float[]? EmitterValue(Emitter em, int kind) => FloatsAt(em.Fx.EmitterTracks, kind, em.Age);

        // the particles as camera-facing / velocity-stretched quads, grouped by texture and blend; scale = Transform's
        public void Collect(List<BrawlFxDraw> draws, Vector3 camPos, Vector3 camRight, Vector3 camUp)
        {
            float unit = Transform.M11 * Transform.M11 + Transform.M12 * Transform.M12 + Transform.M13 * Transform.M13;
            unit = MathF.Sqrt(unit);
            foreach (Particle p in _particles)
            {
                BrawlEffect fx = p.Em.Fx;
                BrawlParticle pd = fx.Particle;
                BrawlEmitter e = fx.Emitter;
                IReadOnlyList<BrawlEffectTrack> tr = fx.ParticleTracks;
                float age = p.Age;
                float[] scale = FloatsAt(tr, 11, age, p.Life) ?? new[] { pd.Scale.X, pd.Scale.Y };
                float w = p.Size.X * scale[0] * unit, h = p.Size.Y * (scale.Length > 1 ? scale[1] : scale[0]) * unit;
                if (w <= 0 || h <= 0) continue;
                BrawlRgba c0 = pd.Colours[0], c1 = pd.Colours[1];
                float[] pri = BytesAt(tr, 0, age, 3, p.Life) ?? new[] { c0.R / 255f, c0.G / 255f, c0.B / 255f };
                float alpha = BytesAt(tr, 1, age, 1, p.Life)?[0] ?? c0.A / 255f;
                float[] sec = BytesAt(tr, 2, age, 3, p.Life) ?? new[] { c1.R / 255f, c1.G / 255f, c1.B / 255f };
                string? tex = pd.TextureNames[0];
                foreach (BrawlEffectTrack t in tr)
                {
                    if (t.Kind != 14 || t.Names.Count == 0) continue;
                    int i = 0;
                    if (t.Keys.Count > 1)
                    {
                        BrawlTrackKey k = Between(t, Fit(t, age, p.Life)).A;
                        if (k.RawValue.Length >= 4) i = (int)BinaryPrimitives.ReadUInt32BigEndian(k.RawValue);
                    }
                    tex = t.Names[Math.Clamp(i, 0, t.Names.Count - 1)];
                }
                Vector3 at = World(p);
                Vector3 right, up;
                if (e.ParticleShape == BrawlEmitter.ShapeDirectional)
                {
                    Vector3 v = p.Follows ? Vector3.TransformNormal(p.Vel, p.Em.World) : p.Vel;
                    if (v.LengthSquared() < 1e-10f) v = at - p.LastWorld;
                    if (v.LengthSquared() < 1e-10f) v = camUp;
                    up = Vector3.Normalize(v);
                    Vector3 side = Vector3.Cross(up, camPos - at);
                    right = side.LengthSquared() > 1e-10f ? Vector3.Normalize(side) : camRight;
                }
                else
                {
                    float s = MathF.Sin(p.Spin), co = MathF.Cos(p.Spin);
                    right = camRight * co + camUp * s;
                    up = camUp * co - camRight * s;
                }
                p.LastWorld = at;
                BrawlFxDraw d = DrawFor(draws, tex, e.DstFactor == 1);
                var pv = new Vector4(pri[0], pri[1], pri[2], alpha);
                var sv = new Vector3(sec[0], sec[1], sec[2]);
                Vector3 hx = right * (w / 2), hy = up * (h / 2);
                Quad(d, at - hx + hy, at + hx + hy, at - hx - hy, at + hx - hy, pv, sv);
            }
        }

        internal static BrawlFxDraw DrawFor(List<BrawlFxDraw> draws, string? tex, bool additive)
        {
            foreach (BrawlFxDraw d in draws) if (d.Texture == tex && d.Additive == additive) return d;
            var n = new BrawlFxDraw { Texture = tex, Additive = additive };
            draws.Add(n);
            return n;
        }

        static void Quad(BrawlFxDraw d, Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br, Vector4 pri, Vector3 sec)
        {
            BrawlFxVertex V(Vector3 p, float u, float v) => new() { Pos = p, Uv = new Vector2(u, v), Primary = pri, Secondary = sec };
            d.Verts.Add(V(tl, 0, 0)); d.Verts.Add(V(tr, 1, 0)); d.Verts.Add(V(bl, 0, 1));
            d.Verts.Add(V(bl, 0, 1)); d.Verts.Add(V(tr, 1, 0)); d.Verts.Add(V(br, 1, 1));
        }
    }

    // A bullet model (the effect list's model file) posed by its CHR0 at a frame, as triangles.
    public static class BrawlFxMeshes
    {
        public static void Collect(BrawlFxModel model, float frame, Matrix4x4 transform, Vector3 camPos, List<BrawlFxDraw> draws, float alpha = 1)
        {
            BrawlMesh mesh = model.Mesh;
            var world = new Dictionary<int, Matrix4x4>();
            Matrix4x4 BoneWorld(BrawlBone b)
            {
                if (world.TryGetValue(b.Number, out Matrix4x4 m)) return m;
                BrawlBonePose pose = model.Animation?.Evaluate(b.Name, model.Animation.Loops && model.Animation.FrameCount > 0
                    ? frame % model.Animation.FrameCount : Math.Min(frame, Math.Max(0, model.Animation.FrameCount - 1)))
                    ?? new BrawlBonePose(b.Scale, b.Rotation, b.Translation);
                const float deg = MathF.PI / 180;
                m = Matrix4x4.CreateScale(pose.Scale) * Matrix4x4.CreateRotationX(pose.Rotation.X * deg)
                    * Matrix4x4.CreateRotationY(pose.Rotation.Y * deg) * Matrix4x4.CreateRotationZ(pose.Rotation.Z * deg)
                    * Matrix4x4.CreateTranslation(pose.Translation);
                BrawlBone? parent = b.Parent >= 0 ? mesh.BoneByNumber(b.Parent) : null;
                m = parent != null ? m * BoneWorld(parent) : m * transform;
                return world[b.Number] = m;
            }
            // see-through steps after the opaque ones, each by priority
            foreach (BrawlDrawStep step in mesh.DrawSteps.OrderBy(s => s.SeeThrough).ThenBy(s => s.Priority))
            {
                if (mesh.ObjectByNumber(step.Object) is not BrawlMeshObject obj || mesh.BoneByNumber(step.Bone) is not BrawlBone bone) continue;
                BrawlVertexArray? pos = mesh.PositionArrayByNumber(obj.PositionArray);
                BrawlVertexArray? tex = obj.Tex0Array < 0 ? null : mesh.TexCoordArrayByNumber(obj.Tex0Array);
                if (pos == null) continue;
                model.Materials.TryGetValue(step.MaterialName, out GxMaterial? mat);
                (string? texName, bool additive, Vector4 pri, Vector3 sec) = Fold(mat);
                pri.W *= alpha;
                Matrix4x4 m = BoneWorld(bone);
                if (bone.FacingMode == 6)
                {
                    // turned about its own Y axis to face the camera
                    Vector3 o = m.Translation, ay = new(m.M21, m.M22, m.M23), ax = new(m.M11, m.M12, m.M13);
                    float sx = ax.Length();
                    Vector3 side = Vector3.Cross(ay, camPos - o);
                    if (side.LengthSquared() > 1e-10f)
                    {
                        side = Vector3.Normalize(side) * sx;
                        m.M11 = side.X; m.M12 = side.Y; m.M13 = side.Z;
                    }
                }
                BrawlFxDraw d = BrawlFxSystem.DrawFor(draws, texName, additive);
                foreach ((BrawlMeshVertex a, BrawlMeshVertex b, BrawlMeshVertex c) in obj.Triangles)
                {
                    foreach (BrawlMeshVertex v in new[] { a, b, c })
                    {
                        Vector2 uv = tex != null && v.Tex0 >= 0 ? new Vector2(tex.Values[v.Tex0].X, tex.Values[v.Tex0].Y) : Vector2.Zero;
                        d.Verts.Add(new BrawlFxVertex { Pos = Vector3.Transform(pos.Values[v.Position], m), Uv = uv, Primary = pri, Secondary = sec });
                    }
                }
            }
        }

        // a one-stage material in the player's form: rgb = lerp(sec, pri, texture) (texture input 8 as the stage's c),
        // else a constant; alpha = pri.a x texture alpha (texture alpha input 4) or constant
        static (string? Tex, bool Additive, Vector4 Pri, Vector3 Sec) Fold(GxMaterial? m)
        {
            if (m == null || m.NumTevStage < 1) return (null, false, Vector4.One, Vector3.One);
            GxTevStage s = m.Stages[0];
            Vector4 ras = new(m.MatColor, m.MatColorA);
            Vector3 C(int input) => input switch
            {
                0 => new(m.Reg[0].X, m.Reg[0].Y, m.Reg[0].Z), 2 => new(m.Reg[1].X, m.Reg[1].Y, m.Reg[1].Z),
                4 => new(m.Reg[2].X, m.Reg[2].Y, m.Reg[2].Z), 6 => new(m.Reg[3].X, m.Reg[3].Y, m.Reg[3].Z),
                10 => new(ras.X, ras.Y, ras.Z), 12 => Vector3.One, 13 => new(0.5f), 8 => Vector3.One, _ => Vector3.Zero,
            };
            float A(int input) => input switch
            {
                1 => m.Reg[1].W, 2 => m.Reg[2].W, 3 => m.Reg[3].W, 5 => ras.W, 4 => 1, _ => 0,
            };
            string? tex = s.TexMap >= 0 ? m.TexNames[s.TexMap] : null;
            Vector3 pri, sec;
            if (s.Cc == 8 && tex != null)
            {
                sec = C(s.Ca);
                pri = C(s.Cb);
            }
            else
            {
                // lerp(a, b, c) + d with the inputs as constants
                Vector3 cc = C(s.Cc);
                pri = sec = C(s.Ca) * (Vector3.One - cc) + C(s.Cb) * cc + C(s.Cd);
                tex = null;
            }
            // alpha: lerp(a, b, c) + d; the texture's alpha (input 4) stands as 1 here and is multiplied in by the shader
            float aa = A(s.Aa), ab = A(s.Ab), ac = A(s.Ac), ad = A(s.Ad);
            float alpha = Math.Clamp(aa * (1 - ac) + ab * ac + ad, 0, 1);
            bool additive = m.BlendEnable && m.BlendDst == 1;
            if (!m.BlendEnable) alpha = 1;
            return (tex, additive, new Vector4(pri, alpha), sec);
        }
    }
}
