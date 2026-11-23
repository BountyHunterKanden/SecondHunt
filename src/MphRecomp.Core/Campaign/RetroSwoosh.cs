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
    // Path mode (Path, set before the first Step; the Hyper Beam's Corruption swooshes, 2026-11-14): each point is made
    // where Place last put the system (in the world, with the system's axes then: Metaforce's SetTranslation /
    // SetOrientation), so the trail stays along the path the shot flew; IVEL / VELM / POFS / NPOS turn with those axes,
    // ROTM and KEYE run on the swoosh's own frame (CParticleGlobals' emitter time), RRAD sizes the right side (LLRD: LRAD),
    // and it draws the kinds Corruption's shots use: Render3SidedSolidNoSplineNoGaps, Render2SidedNoSplineNoGaps (ORNT:
    // facing the camera) and RenderNSidedSpline (any SIDE, a Catmull-Rom spline of SPLN steps a segment, CROS). Without
    // Path, all as above.
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
            public Vector3 Ax, Ay, Az; // path mode: the system's axes when it was made
            public float Right;        // path mode: the right side's radius
        }

        readonly Part.Swoosh _d;
        readonly Random _rand;
        readonly Point[] _pts;
        readonly int _pslt, _leng, _side, _spln;
        readonly bool _tube, _wire;
        int _cur, _frame, _count;
        // path mode: where the system is (Place), and the swoosh's frame for KEYE (-1: off)
        Vector3 _at, _ax = Vector3.UnitX, _ay = Vector3.UnitY, _az = Vector3.UnitZ;
        int _ef = -1;
        Vector3[] _sp = Array.Empty<Vector3>();

        public bool Emitting = true;
        public bool Path;
        public uint Texture => _d.Texture;
        public bool Additive => _d.Flag("AALP") || (_d["SBDM"]?.I ?? 0) != 0; // Corruption: SBDM, a blend mode (0 alpha)
        // the blend and the bloom it marks (Corruption: SBDM, RetroBlends.Mp3), else AALP: added or alpha blended
        public (RetroBlend Blend, float Glow) Mode => _d["SBDM"] is Part.Elem m ? RetroBlends.Mp3(m.I)
            : (_d.Flag("AALP") ? RetroBlend.Add : RetroBlend.Alpha, 0);
        // drawn by this runner (SIDE 3, no spline, at least two points; path mode: any SIDE past 1)
        public bool Drawable => Path ? _leng >= 2 && !_wire && _side >= 2 : _tube;
        // IsSystemDeletable
        public bool Done => !(Emitting && _frame < _pslt) && _count < 2;

        public RetroSwoosh(Part.Swoosh desc, int seed)
        {
            _d = desc;
            _rand = new Random(seed);
            _leng = Int(_d["LENG"], 0) + 1;
            _side = Int(_d["SIDE"], 0);
            _spln = Int(_d["SPLN"], 0);
            _wire = _d.Flag("WIRE");
            _pts = new Point[Math.Max(2, _leng)];
            _pslt = _d["PSLT"] != null ? Int(_d["PSLT"], 0) : 0x7fffff;
            _tube = _leng >= 2 && _side == 3 && _spln <= 0 && !_wire;
        }

        // path mode: the system's frame (effect space to the world: rows 0-2 its axes with its scale, row 3 where it is)
        public void Place(Matrix4 frame)
        {
            _at = frame.Row3.Xyz;
            _ax = frame.Row0.Xyz;
            _ay = frame.Row1.Xyz;
            _az = frame.Row2.Xyz;
        }

        // a vector in effect space to the world through a point's axes (path mode; as it is otherwise)
        Vector3 W(in Point p, Vector3 v) => Path ? p.Ax * v.X + p.Ay * v.Y + p.Az * v.Z : v;

        // one 60 Hz frame (CParticleSwoosh::Update + UpdateTranslationAndOrientation)
        public void Step()
        {
            int life = _pts.Length;
            if (Path) _ef = _frame;
            if (++_cur >= _pts.Length) _cur = 0;
            ref Point s = ref _pts[_cur];
            if (Emitting && _frame < _pslt)
            {
                s.Pos = Path ? _at : Vector3.Zero;
                s.Rot = Real(_d["IROT"], 0, life, 0);
                s.Rotm = 0;
                s.Start = _frame;
                if (!s.Active)
                {
                    _count++;
                    s.Active = true;
                }
                s.Ax = _ax;
                s.Ay = _ay;
                s.Az = _az;
                s.Vel = W(s, Vec(_d["IVEL"], 0, life));
                s.Offset = W(s, Vec(_d["POFS"], 0, life));
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
                bool llrd = _d.Flag("LLRD") || _d["RRAD"] == null;
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
                    else p.Rotm = Real(_d["ROTM"], Path ? _frame : f, life, 0);
                    Mod(_d["VELM"], f, life, ref p);
                    Mod(_d["VLM2"], f, life, ref p);
                    if (f > 0) p.Pos += p.Vel;
                    if (_d["NPOS"] != null) p.UseOffset = p.Offset + W(p, Vec(_d["NPOS"], f, life));
                    if (_d["COLR"] != null) p.Color = Color(_d["COLR"], f, life);
                    p.Radius = Real(_d["LRAD"], f, life, 0);
                    if (Path) p.Right = llrd ? p.Radius : Real(_d["RRAD"], f, life, 0);
                }
            }
            _frame++;
        }

        // the swoosh's triangles, effect space (path mode: the world the points were made in) taken to the world by
        // toWorld; camera: where the camera is in the world (path mode's ORNT ribbons face it). The tube: 3 faces a
        // segment, 2 triangles a face, the texture's u running 0..1 along each repeat, v 0..1 across a face
        public void Triangles(Matrix4 toWorld, List<Vertex> into, Vector3 camera = default)
        {
            if (!Drawable || _count < 2) return;
            int tspn = _d["TSPN"] != null ? Int(_d["TSPN"], 0) : 0;
            if (tspn <= 0) tspn = _pts.Length - 1;
            float span = 1f / tspn;
            if (Path && !(_side == 3 && _spln <= 0))
            {
                if (_side == 2 && _spln <= 0) Ribbon(toWorld, into, span, camera);
                else Spline(toWorld, into, span, tspn);
                return;
            }
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
                Vector3 off = Center(s);
                if (Path)
                {
                    // Render3SidedSolidNoSplineNoGaps' corners: (sin, 0, cos) x LRAD through the point's axes
                    p0[cur] = Vector3.TransformPosition(TubeCorner(s, angle) + off, toWorld);
                    p1[cur] = Vector3.TransformPosition(TubeCorner(s, angle + 2.0943952f) + off, toWorld);
                    p2[cur] = Vector3.TransformPosition(TubeCorner(s, angle + 4.1887903f) + off, toWorld);
                }
                else
                {
                    p0[cur] = Vector3.TransformPosition(Corner(angle, s.Radius) + off, toWorld);
                    p1[cur] = Vector3.TransformPosition(Corner(angle + 2.0943952f, s.Radius) + off, toWorld);
                    p2[cur] = Vector3.TransformPosition(Corner(angle + 4.1887903f, s.Radius) + off, toWorld);
                }
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

        // the points' centres (through toWorld), radii (LRAD, times its scale) and colours, newest first, a radius below 0
        // where an inactive one breaks the trail: for a glow drawn along it
        public void Spine(Matrix4 toWorld, List<(Vector3 Pos, float Radius, Vector4 Color)> into)
        {
            if (!Drawable || _count < 2) return;
            float scale = toWorld.Row0.Xyz.Length; // (path mode: and the point's own axes, as the triangles')
            int idx = _cur;
            for (int i = 0; i < _pts.Length; i++)
            {
                Point s = _pts[idx];
                if (--idx < 0) idx = _pts.Length - 1;
                into.Add(s.Active ? (Vector3.TransformPosition(Center(s), toWorld), s.Radius * scale * (Path ? s.Ax.Length : 1), s.Color) : (default, -1, default));
            }
        }

        static Vector3 TubeCorner(in Point s, float angle) => s.Ax * (s.Radius * MathF.Sin(angle)) + s.Az * (s.Radius * MathF.Cos(angle));

        int Wrap(int i) => (i % _pts.Length + _pts.Length) % _pts.Length;

        // a point's centre as drawn
        static Vector3 Center(in Point s) => s.Pos + s.UseOffset;

        // Render2SidedNoSplineNoGaps: a ribbon, each point a pair LRAD one way and RRAD the other: across the trail and
        // the line to the camera (ORNT), or along IROT + ROTM in the point's XZ plane; each point's own colour, u running on
        // 1 / TSPN a point and starting again past 1
        void Ribbon(Matrix4 toWorld, List<Vertex> into, float span, Vector3 camera)
        {
            bool ornt = _d.Flag("ORNT");
            Vector3 cam = Vector3.TransformPosition(camera, toWorld.Inverted());
            Vector3 dot = Vector3.Zero, pa = default, pb = default;
            Vector4 pc = default;
            float u = 0, pu = 0;
            bool have = false;
            int idx = _cur;
            for (int i = 0; i < _pts.Length; i++, idx = Wrap(idx - 1))
            {
                Point s = _pts[idx];
                if (!s.Active)
                {
                    have = false;
                    continue;
                }
                Vector3 at = Center(s), a, b;
                if (ornt)
                {
                    int older = Wrap(idx - 1), newer = Wrap(idx + 1);
                    Vector3 delta = older != _cur && _pts[older].Active ? _pts[older].Pos - s.Pos : s.Pos - _pts[newer].Pos;
                    Vector3 cross = Vector3.Cross(delta, cam - at);
                    cross = cross.LengthSquared > 1e-12f ? cross.Normalized() : dot;
                    if (Vector3.Dot(cross, dot) < 0) cross = -cross;
                    dot = cross;
                    float k = s.Ax.Length; // the radii are effect space's (the axes carry its scale)
                    a = dot * (s.Radius * k) + at;
                    b = -dot * (s.Right * k) + at;
                }
                else
                {
                    float angle = MathF.PI * (s.Rot + s.Rotm) / 180f;
                    Vector3 v = s.Ax * MathF.Sin(angle) + s.Az * MathF.Cos(angle);
                    a = v * s.Radius + at;
                    b = -v * s.Right + at;
                }
                a = Vector3.TransformPosition(a, toWorld);
                b = Vector3.TransformPosition(b, toWorld);
                if (have)
                {
                    var v0 = new Vertex(pa, new Vector2(pu, 0), pc);
                    var v1 = new Vertex(pb, new Vector2(pu, 1), pc);
                    var v2 = new Vertex(b, new Vector2(u, 1), s.Color);
                    var v3 = new Vertex(a, new Vector2(u, 0), s.Color);
                    into.Add(v0); into.Add(v1); into.Add(v2);
                    into.Add(v0); into.Add(v2); into.Add(v3);
                }
                if (u >= 1) u -= 1; // the strip starts again here, at u - 1
                pa = a;
                pb = b;
                pc = s.Color;
                pu = u;
                have = true;
                u += span;
            }
        }

        // RenderNSidedSpline: each segment (a point and the next older) as SPLN + 1 steps of a Catmull-Rom spline through
        // its neighbours' cross-sections, SIDE corners round (corner k at 360 / SIDE x k + IROT + ROTM: LRAD from past 0 to
        // 180, RRAD the rest), a face between each corner and the next (CROS: the one opposite, SIDE / 2 faces); the
        // segment's colour its newer point's, u from (segment % TSPN) / TSPN over 1 / TSPN
        void Spline(Matrix4 toWorld, List<Vertex> into, float span, int tspn)
        {
            int side = Math.Max(1, _side), spln = Math.Max(0, _spln), n = _pts.Length;
            bool cros = _d.Flag("CROS");
            int faces = side <= 2 ? 1 : cros ? side / 2 : side;
            float seg = span / (spln + 1);
            if (_sp.Length < 4 * side) _sp = new Vector3[4 * side];
            int idx = _cur;
            for (int i = 0; i < n - 1; i++, idx = Wrap(idx - 1))
            {
                int prev = Wrap(idx - 1);
                if (!_pts[idx].Active || !_pts[prev].Active) continue;
                int j0 = Wrap(idx + 1), j3 = Wrap(idx - 2);
                if (idx == _cur || !_pts[j0].Active) j0 = idx;
                if (j3 == _cur || !_pts[j3].Active) j3 = prev;
                if (n <= 2)
                {
                    j0 = idx;
                    j3 = prev;
                }
                Section(0, _pts[j0], side);
                Section(1, _pts[idx], side);
                Section(2, _pts[prev], side);
                Section(3, _pts[j3], side);
                Vector4 color = _pts[idx].Color;
                float xMin = i % tspn * span;
                for (int st = 0; st <= spln; st++)
                {
                    float t0 = st / (float)(spln + 1), t1 = (st + 1) / (float)(spln + 1);
                    float xMax = xMin + seg;
                    for (int k = 0; k < faces; k++)
                    {
                        int o = cros ? k + side / 2 : (k + 1) % side;
                        Vector3 a = Vector3.TransformPosition(Spline(k, side, t0), toWorld);
                        Vector3 b = Vector3.TransformPosition(Spline(o, side, t0), toWorld);
                        Vector3 c = Vector3.TransformPosition(Spline(o, side, t1), toWorld);
                        Vector3 d = Vector3.TransformPosition(Spline(k, side, t1), toWorld);
                        // the quad's texture corners as the game sends them
                        var v0 = new Vertex(a, new Vector2(xMin, 0), color);
                        var v1 = new Vertex(b, new Vector2(xMin, 1), color);
                        var v2 = new Vertex(c, new Vector2(xMax, 0), color);
                        var v3 = new Vertex(d, new Vector2(xMax, 1), color);
                        into.Add(v0); into.Add(v1); into.Add(v2);
                        into.Add(v0); into.Add(v2); into.Add(v3);
                    }
                    xMin = xMax;
                }
            }
        }

        // a point's cross-section corners (RenderNSidedSpline) into _sp[j * side ..]
        void Section(int j, in Point s, int side)
        {
            for (int k = 0; k < side; k++)
            {
                float deg = 360f / side * k;
                float angle = MathF.PI * (deg + s.Rot + s.Rotm) / 180f;
                float r = deg > 0 && deg <= 180 ? s.Radius : s.Right;
                _sp[j * side + k] = s.Ax * (r * MathF.Cos(angle)) + s.Az * (r * MathF.Sin(angle)) + Center(s);
            }
        }

        // corner k's Catmull-Rom spline at t between sections 1 (t 0) and 2 (t 1)
        Vector3 Spline(int k, int side, float t)
        {
            Vector3 p0 = _sp[k], p1 = _sp[side + k], p2 = _sp[2 * side + k], p3 = _sp[3 * side + k];
            if (t <= 0) return p1;
            if (t >= 1) return p2;
            float t2 = t * t, t3 = t2 * t;
            return p0 * (-0.5f * t3 + t2 - 0.5f * t) + p1 * (1.5f * t3 - 2.5f * t2 + 1) + p2 * (-1.5f * t3 + 2 * t2 + 0.5f * t)
                + p3 * (0.5f * t3 - 0.5f * t2);
        }

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

        // KEYE / KEYP; path mode's KEYE on the swoosh's own frame (_ef), as Metaforce's emitter time
        float Keyed(Part.Elem e, int frame, int life, int width, int lane)
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
            int f = _ef >= 0 ? _ef : frame;
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
                case "RNDV" when Path: // Corruption: a random direction, its length the real
                {
                    float len = Real(e.Kid(0), frame, life, 0);
                    Vector3 v;
                    do v = new Vector3(Rnd() * 2 - 1, Rnd() * 2 - 1, Rnd() * 2 - 1);
                    while (v.LengthSquared > 1 || v.LengthSquared < 1e-6f);
                    return v.Normalized() * len;
                }
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
                case "CFDL" when Path: // Corruption: the first colour to the second over the point's life
                    return Vector4.Lerp(Color(e.Kid(0), frame, life), Color(e.Kid(1), frame, life), Math.Min(1f, frame / (float)Math.Max(1, life)));
            }
            return Vector4.One;
        }

        // a mod-vector element on a point's velocity and position (GRAV: the velocity gains it each frame)
        void Mod(Part.Elem? e, int frame, int life, ref Point p)
        {
            if (e == null) return;
            switch (e.Kind)
            {
                case "GRAV": p.Vel += W(p, Vec(e.Kid(0), frame, life)); break;
                case "CNST": p.Vel = W(p, new Vector3(Real(e.Kid(0), frame, life, 0), Real(e.Kid(1), frame, life, 0), Real(e.Kid(2), frame, life, 0))); break;
            }
        }
    }
}
