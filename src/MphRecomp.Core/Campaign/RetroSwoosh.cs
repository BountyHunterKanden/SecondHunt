using System;
using System.Collections.Generic;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // A Prime 1 swoosh (SWHC, parsed by Core Import/Retro/Part.cs: Part.Swoosh) run the way the game runs it: Metaforce's
    // CParticleSwoosh (a reverse-engineered Prime 1 engine, https://github.com/AxioDL/metaforce, MIT, Metaforce Contributors).
    // A ring of LENG + 1 points; each frame the next slot gets a new point where its system is (effect space's origin: the
    // Pirate Blaster's shot keeps its systems riding the bolt, WPSC AP11) with its IVEL / POFS / COLR / IROT, or, no longer
    // emitting, is dropped (the trail shortens from its end); every point is then moved by VELM / VLM2 (GRAV here) and its
    // velocity, and coloured (COLR) and sized (LRAD) by its own age over LENG + 1 frames. Drawn as
    // Render3SidedSolidNoSplineNoGaps draws it: a three-sided tube, each point a triangle of radius LRAD at angle IROT + ROTM
    // in effect space's XZ plane (+Y: the system's forward), newest to oldest, TEXR along it (one repeat every 1 / TSPN
    // segments), each segment's colours its two points' (the newest point's own colour is never taken: the game's first
    // segment fades in from nothing, kept). Only what the Pirate Blaster's swooshes use: SIDE 3 without a spline (other kinds
    // draw nothing), TIME 1 (one point a frame), no wireframe. Effect space = Prime's metres, Z up. Draw only.
    // Added for the Pirate Blaster thread (2026-10-10); Core Campaign/RetroParticles.cs runs the PARTs around it.
    public sealed class RetroSwoosh
    {
        public readonly record struct Vertex(Vector3 Pos, Vector2 Uv, Vector4 Color);

        struct Point
        {
            public bool Active;
            public int Start;
            public Vector3 Pos, Vel, Offset, UseOffset;
            public Vector4 Color;
            public float Radius, Rot, Rotm;
        }

        readonly Part.Swoosh _d;
        readonly Random _rand;
        readonly Point[] _pts;
        readonly int _pslt;
        int _cur, _frame, _count;

        public bool Emitting = true;
        public uint Texture => _d.Texture;
        public bool Additive => _d.Flag("AALP");
        // drawn by this runner (SIDE 3, no spline, at least two points)
        public bool Drawable { get; }
        // IsSystemDeletable
        public bool Done => !(Emitting && _frame < _pslt) && _count < 2;

        public RetroSwoosh(Part.Swoosh desc, int seed)
        {
            _d = desc;
            _rand = new Random(seed);
            int leng = Int(_d["LENG"], 0) + 1;
            int side = Int(_d["SIDE"], 0);
            _pts = new Point[Math.Max(2, leng)];
            _pslt = _d["PSLT"] != null ? Int(_d["PSLT"], 0) : 0x7fffff;
            Drawable = leng >= 2 && side == 3 && Int(_d["SPLN"], 0) <= 0 && !_d.Flag("WIRE");
        }

        // one 60 Hz frame (CParticleSwoosh::Update + UpdateTranslationAndOrientation)
        public void Step()
        {
            int life = _pts.Length;
            if (++_cur >= _pts.Length) _cur = 0;
            ref Point s = ref _pts[_cur];
            if (Emitting && _frame < _pslt)
            {
                s.Pos = Vector3.Zero;
                s.Rot = Real(_d["IROT"], 0, life, 0);
                s.Rotm = 0;
                s.Start = _frame;
                if (!s.Active)
                {
                    _count++;
                    s.Active = true;
                }
                s.Vel = Vec(_d["IVEL"], 0, life);
                s.Offset = Vec(_d["POFS"], 0, life);
                s.UseOffset = s.Offset;
                s.Color = _d["COLR"] != null ? Color(_d["COLR"], 0, life) : Vector4.One;
            }
            else if (s.Active)
            {
                _count = Math.Max(0, _count - 1);
                s.Active = false;
            }
            if (_count > 0)
            {
                bool srot = _d.Flag("SROT");
                for (int i = 0; i < _pts.Length; i++)
                {
                    ref Point p = ref _pts[i];
                    if (!p.Active) continue;
                    int f = _frame - p.Start;
                    if (srot)
                    {
                        p.Rot = Real(_d["IROT"], f, life, p.Rot);
                        p.Rotm = 0;
                    }
                    else p.Rotm = Real(_d["ROTM"], f, life, 0);
                    Mod(_d["VELM"], f, life, ref p.Vel, ref p.Pos);
                    Mod(_d["VLM2"], f, life, ref p.Vel, ref p.Pos);
                    if (f > 0) p.Pos += p.Vel;
                    if (_d["NPOS"] != null) p.UseOffset = p.Offset + Vec(_d["NPOS"], f, life);
                    if (_d["COLR"] != null) p.Color = Color(_d["COLR"], f, life);
                    p.Radius = Real(_d["LRAD"], f, life, 0);
                }
            }
            _frame++;
        }

        // the tube's triangles (3 faces a segment, 2 triangles a face), effect space taken to the world by toWorld; the
        // texture's u runs 0..1 along each repeat, v 0..1 across a face
        public void Triangles(Matrix4 toWorld, List<Vertex> into)
        {
            if (!Drawable || _count < 2) return;
            int tspn = _d["TSPN"] != null ? Int(_d["TSPN"], 0) : 0;
            if (tspn <= 0) tspn = _pts.Length - 1;
            float span = 1f / tspn;
            var p0 = new Vector3[2];
            var p1 = new Vector3[2];
            var p2 = new Vector3[2];
            int cur = 0, prev = 1;
            Vector4 color = Vector4.Zero, prevColor;
            bool lastActive = false;
            float uv = -span;
            int idx = _cur;
            for (int i = 0; i < _pts.Length; i++)
            {
                Point s = _pts[idx];
                if (--idx < 0) idx = _pts.Length - 1;
                float angle = MathF.PI * (s.Rot + s.Rotm) / 180f;
                Vector3 off = s.Pos + s.UseOffset;
                p0[cur] = Vector3.TransformPosition(Corner(angle, s.Radius) + off, toWorld);
                p1[cur] = Vector3.TransformPosition(Corner(angle + 2.0943952f, s.Radius) + off, toWorld);
                p2[cur] = Vector3.TransformPosition(Corner(angle + 4.1887903f, s.Radius) + off, toWorld);
                prevColor = color;
                if (!s.Active) lastActive = false;
                else if (!lastActive) lastActive = true;
                else
                {
                    color = s.Color;
                    float prevUv = uv;
                    uv += span;
                    float bas = MathF.Floor(prevUv + 1e-4f);
                    float u0 = prevUv - bas, u1 = uv - bas;
                    Face(into, p0[cur], p1[cur], p1[prev], p0[prev], u1, u0, color, prevColor);
                    Face(into, p1[cur], p2[cur], p2[prev], p1[prev], u1, u0, color, prevColor);
                    Face(into, p2[cur], p0[cur], p0[prev], p2[prev], u1, u0, color, prevColor);
                }
                cur ^= 1;
                prev ^= 1;
            }
        }

        static Vector3 Corner(float angle, float r) => new(r * MathF.Cos(angle), 0, r * MathF.Sin(angle));

        // a quad as the game sends it: (cur, v 0), (cur, v 1), (prev, v 1), (prev, v 0)
        static void Face(List<Vertex> into, Vector3 a, Vector3 b, Vector3 c, Vector3 d, float uCur, float uPrev, Vector4 cCur, Vector4 cPrev)
        {
            var va = new Vertex(a, new Vector2(uCur, 0), cCur);
            var vb = new Vertex(b, new Vector2(uCur, 1), cCur);
            var vc = new Vertex(c, new Vector2(uPrev, 1), cPrev);
            var vd = new Vertex(d, new Vector2(uPrev, 0), cPrev);
            into.Add(va); into.Add(vb); into.Add(vc);
            into.Add(va); into.Add(vc); into.Add(vd);
        }

        // ---- the elements the swooshes use (as RetroParticles evaluates them; frame = the point's age, life = LENG + 1) ----

        float Rnd() => (float)_rand.NextDouble();

        static float Keyed(Part.Elem e, int frame, int life, int width, int lane)
        {
            float[] k = e.Keys!;
            int n = k.Length / width;
            if (n == 0) return 0;
            if (e.Percent)
            {
                float pct = frame * 100f / Math.Max(1, life);
                int i = (int)pct;
                if (i >= n - 1) return k[(n - 1) * width + lane];
                float t = pct - i;
                return k[i * width + lane] + (k[(i + 1) * width + lane] - k[i * width + lane]) * t;
            }
            int f = frame;
            if (e.Loop && f >= e.LoopEnd && e.LoopEnd > e.LoopStart) f = (f - e.LoopStart) % (e.LoopEnd - e.LoopStart) + e.LoopStart;
            return k[Math.Clamp(f, 0, n - 1) * width + lane];
        }

        int Int(Part.Elem? e, int dflt)
        {
            if (e == null) return dflt;
            return e.Kind switch
            {
                "CNST" => e.I,
                "KEYE" or "KEYP" => (int)Keyed(e, 0, 1, 1, 0),
                _ => dflt,
            };
        }

        float Real(Part.Elem? e, int frame, int life, float dflt)
        {
            if (e == null) return dflt;
            switch (e.Kind)
            {
                case "CNST": return e.F;
                case "KEYE": case "KEYP": return Keyed(e, frame, life, 1, 0);
                case "RAND": case "IRND":
                {
                    float a = Real(e.Kid(0), frame, life, 0), b = Real(e.Kid(1), frame, life, 0);
                    return a + (b - a) * Rnd();
                }
                case "LFTW": case "ISWT":
                {
                    float a = Real(e.Kid(0), frame, life, 0), b = Real(e.Kid(1), frame, life, 0);
                    return a + (b - a) * Math.Min(1f, frame / (float)Math.Max(1, life));
                }
                case "ADD_": return Real(e.Kid(0), frame, life, 0) + Real(e.Kid(1), frame, life, 0);
                case "SUB_": return Real(e.Kid(0), frame, life, 0) - Real(e.Kid(1), frame, life, 0);
                case "MULT": return Real(e.Kid(0), frame, life, 0) * Real(e.Kid(1), frame, life, 0);
                case "SCAL": return Real(e.Kid(0), frame, life, 0) * frame;
            }
            return dflt;
        }

        Vector3 Vec(Part.Elem? e, int frame, int life)
        {
            if (e == null) return Vector3.Zero;
            switch (e.Kind)
            {
                case "CNST": return new Vector3(Real(e.Kid(0), frame, life, 0), Real(e.Kid(1), frame, life, 0), Real(e.Kid(2), frame, life, 0));
                case "KEYE": case "KEYP": return new Vector3(Keyed(e, frame, life, 3, 0), Keyed(e, frame, life, 3, 1), Keyed(e, frame, life, 3, 2));
                case "ADD_": return Vec(e.Kid(0), frame, life) + Vec(e.Kid(1), frame, life);
                case "SUB_": return Vec(e.Kid(0), frame, life) - Vec(e.Kid(1), frame, life);
                case "MULT": return Vec(e.Kid(0), frame, life) * Vec(e.Kid(1), frame, life);
            }
            return Vector3.Zero;
        }

        Vector4 Color(Part.Elem? e, int frame, int life)
        {
            if (e == null) return Vector4.One;
            switch (e.Kind)
            {
                case "CNST":
                    return new Vector4(Real(e.Kid(0), frame, life, 0), Real(e.Kid(1), frame, life, 0), Real(e.Kid(2), frame, life, 0),
                        Real(e.Kid(3), frame, life, 0));
                case "KEYE": case "KEYP":
                    return new Vector4(Keyed(e, frame, life, 4, 0), Keyed(e, frame, life, 4, 1), Keyed(e, frame, life, 4, 2), Keyed(e, frame, life, 4, 3));
                case "FADE":
                {
                    Vector4 a = Color(e.Kid(0), frame, life), b = Color(e.Kid(1), frame, life);
                    float end = Real(e.Kid(2), frame, life, 0);
                    float t = end > 0 ? frame / end : 1;
                    return t > 1 ? b : Vector4.Lerp(a, b, t);
                }
            }
            return Vector4.One;
        }

        // a mod-vector element on a point's velocity and position (GRAV: the velocity gains it each frame)
        void Mod(Part.Elem? e, int frame, int life, ref Vector3 vel, ref Vector3 pos)
        {
            if (e == null) return;
            switch (e.Kind)
            {
                case "GRAV": vel += Vec(e.Kid(0), frame, life); break;
                case "CNST": vel = new Vector3(Real(e.Kid(0), frame, life, 0), Real(e.Kid(1), frame, life, 0), Real(e.Kid(2), frame, life, 0)); break;
            }
        }
    }
}
