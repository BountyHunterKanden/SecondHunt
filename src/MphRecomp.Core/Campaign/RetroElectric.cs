using System;
using System.Collections.Generic;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // A Retro electric effect (ELSC, Core Import/Retro/Elsc.cs) run the way Retro's engine runs one (the behaviour as the
    // public notes and the open-source metaforce decompilation document it, docs/MP3_HYPER_EFFECTS_FORMATS.md section 3,
    // in our own code): every frame GRAT new bolts (the remainder carried) up to SCNT alive, each living SLIF frames. A
    // bolt's shape is worked out once, when it is born, and kept for its life: a curve from its IEMT point to its FEMT
    // point (the emitters' velocities its control points), SSEG points along it at jittered steps, then pushed sideways
    // by a midpoint fractal (AMPL, AMPD) that zigzags the whole bolt in one random plane. It is drawn as up to three
    // lines over its points, LWDn wide in 480-line pixels, LCLn coloured by its age (3: the wide glow, 1: the thin
    // bright core). A bolt keeps its shape (its own seed) and its start in effect space, so it rides along as Transform
    // moves. Spawn adds one whose end the caller places and holds on a world point (Dark Samus's Hyper Ball: arcs to the
    // surfaces around it and to its targets, as Corruption's code adds them; CampaignRenderer.HyperFx.cs).
    // Effect space = Prime metres, Z up; Transform takes it to the world. Added for the Dark Samus Hyper Mode thread,
    // 2026-10-12; reworked to Retro's shapes the same day (owner: the arcs looked like Shock Coil's rigid zigzags).
    public sealed class RetroElectric
    {
        // the emitters' velocities times this (1 = as the effect has them, Retro's way); a dev build's --es hyperbend
        // sets it (SoloArena.cs) to compare
        public static float Bend = 1f;

        // Layer 3: the glow, 1: the core; Color: LCLn (Corruption adds its RGB whole and uses the alpha only to drop a
        // line once it is 0 as a byte, MP3 main.dol RenderLines 0x803a2a14); Texture: TEXR along the line (0: none)
        public readonly record struct Line(Vector3[] Points, float WidthPx, Vector4 Color, int Layer, uint Texture);

        sealed class Bolt
        {
            public Vector3 P0, V0, P1, V1;      // effect space
            public Func<Vector3>? EndAt;        // the end held on a world point (a surface, a target) as things move
            public int Seed, Age, Life;
            public Vector3[] Points = Array.Empty<Vector3>();   // effect space, this frame
            public readonly float[] Width = new float[3];
        }

        readonly Elsc _d;
        readonly Random _rand;
        readonly List<Bolt> _live = new();
        readonly int _most, _segs;
        float _remainder;
        int _frame;

        public Matrix4 Transform = Matrix4.Identity;
        public bool Emitting = true;
        public bool Additive => (_d["BLDM"]?.I ?? 1) != 0;
        public uint Texture => _d.Texture;
        public int Live => _live.Count;
        public int Most => _most;
        // GRAT: new bolts a frame
        public float Rate => Real(_d["GRAT"], 0, 0, 1);

        public RetroElectric(Elsc d, int seed)
        {
            _d = d;
            _rand = new Random(seed);
            // both once, as Retro does (its defaults: 1 bolt, 8 segments; at most 32 bolts)
            _most = Math.Clamp(Int(_d["SCNT"], 1), 0, 32);
            _segs = Math.Clamp(Int(_d["SSEG"], 8), 1, 64);
        }

        public bool Done => !Emitting && _live.Count == 0;

        // one 60 Hz frame (Transform set first): the live bolts age (their widths re-rolled and held ends followed, each
        // keeping its shape from its own seed, as Corruption redraws its arcs), then the emitter's new ones
        public void Step()
        {
            Matrix4? toEffect = null;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Bolt b = _live[i];
                if (++b.Age >= b.Life)
                {
                    _live.RemoveAt(i);
                    continue;
                }
                Widths(b);
                if (b.EndAt != null)
                {
                    toEffect ??= Transform.Inverted();
                    b.P1 = Vector3.TransformPosition(b.EndAt(), toEffect.Value);
                    b.Points = Shape(b);
                }
            }
            int life = Int(_d["LIFE"], Int32.MaxValue);
            if (Emitting && _frame < life)
            {
                _remainder += Math.Max(0, Rate);
                int n = (int)MathF.Floor(_remainder);
                _remainder -= n;
                for (int i = 0; i < n && _live.Count < _most; i++)
                {
                    (Vector3 p0, Vector3 v0) = Start();
                    (Vector3 p1, Vector3 v1) = End();
                    Add(p0, v0, p1, v1, null);
                }
            }
            _frame++;
        }

        // the emitters' own start and end (effect space): a point and a velocity each, read at the effect's frame as
        // CParticleElectric::CalculatePoints reads them (Echoes' bomb slot bolts pulse their ends' x in opposite phase,
        // PULS: so the bolts cross corner to corner, an X)
        public (Vector3 Pos, Vector3 Vel) Start() => Emit(_d["IEMT"], Vector3.Zero);
        public (Vector3 Pos, Vector3 Vel) End() => Emit(_d["FEMT"], Vector3.UnitY);

        // one bolt now from a start and velocity (effect space) to a world point it stays on, ending with a velocity
        // (effect space); past SCNT; living `life` frames (0: SLIF's). Returns its life in frames.
        public int Spawn(Vector3 p0, Vector3 v0, Func<Vector3> endAt, Vector3 v1, int life = 0)
            => Add(p0, v0, Vector3.TransformPosition(endAt(), Transform.Inverted()), v1, endAt, life).Life;

        Bolt Add(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, Func<Vector3>? endAt, int life = 0)
        {
            var b = new Bolt
            {
                P0 = p0, V0 = v0 * Bend, P1 = p1, V1 = v1 * Bend, EndAt = endAt, Seed = _rand.Next(),
                Life = Math.Max(1, life > 0 ? life : Int(_d["SLIF"], 1)),
            };
            b.Points = Shape(b);
            Widths(b);
            _live.Add(b);
            return b;
        }

        void Widths(Bolt b)
        {
            for (int k = 0; k < 3; k++) b.Width[k] = Real(_d["LWD" + (k + 1)], 0, b.Age, b.Life);
        }

        // Retro's bolt shape: the curve's points at jittered steps, then the midpoint fractal across it
        Vector3[] Shape(Bolt bolt)
        {
            var r = new Random(bolt.Seed);
            (Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1) = (bolt.P0, bolt.V0, bolt.P1, bolt.V1);
            int segs = _segs;
            var pts = new Vector3[segs + 1];
            float step = 1f / segs;
            bool moving0 = v0 != Vector3.Zero, moving1 = v1 != Vector3.Zero;
            if (moving0 || moving1)
            {
                // the control points: each end's velocity from it (one end still: the other's twice)
                Vector3 c0 = moving0 ? p0 + v0 : p1 + v1 * 2, c1 = moving1 ? p1 + v1 : p0 + v0 * 2;
                pts[0] = p0;
                for (int i = 1; i < segs; i++)
                {
                    float t = (i + Range(r, -0.45f, 0.45f)) * step, u = 1 - t;
                    pts[i] = u * u * u * p0 + 3 * u * u * t * c0 + 3 * u * t * t * c1 + t * t * t * p1;
                }
            }
            else
            {
                // a straight bolt, each point jittered along it (one step behind, as Retro's is)
                Vector3 d = (p1 - p0) * step;
                pts[0] = p0;
                for (int i = 1; i < segs; i++) pts[i] = p0 + d * (i - 1 + Range(r, -0.45f, 0.45f));
            }
            pts[segs] = p1;
            // the fractal: a magnitude per point, the ends 0, each midpoint its ends' average plus a random share of AMPL
            // (bigger for longer spans) and of AMPD (the same everywhere: the fine crackle)
            var mags = new float[segs + 1];
            float ampl = _d["AMPL"] != null ? Real(_d["AMPL"], 1, 0, 1) * 2 : 1;
            float ampd = Real(_d["AMPD"], 0, 0, 1);
            Fractal(r, mags, 0, segs, ampl, ampd);
            // ... pushed along one side of the bolt (its bend's plane), turned about each segment by one random angle per
            // bolt and a little more where the push is big
            Vector3 up = Vector3.UnitZ;
            Vector3 a = pts[0] - pts[1], b = pts[segs] - pts[1];
            if (a.LengthSquared > 1e-12f && b.LengthSquared > 1e-12f)
            {
                a.Normalize();
                b.Normalize();
                up = MathF.Abs(Vector3.Dot(a, b)) > 0.9999f ? LookUp(pts[0], pts[1]) : Vector3.Cross(a, b).Normalized();
            }
            else if (pts[0] != pts[1])
            {
                up = LookUp(pts[0], pts[1]);
            }
            float common = Range(r, 0, 360);
            var push = new Vector3[segs + 1];
            for (int i = 1; i < segs; i++)
            {
                Vector3 along = pts[i] - pts[i - 1];
                if (along.LengthSquared < 1e-12f) continue;
                float deg = (ampl != 0 ? mags[i] / ampl * 16 * Range(r, -1, 1) : 0) + common;
                push[i] = Vector3.Transform(mags[i] * up, Quaternion.FromAxisAngle(along.Normalized(), MathHelper.DegreesToRadians(deg)));
            }
            for (int i = 1; i < segs; i++) pts[i] += push[i];
            if (_d.Flag("ZERY"))
            {
                for (int i = 0; i < pts.Length; i++) pts[i].Y = 0;
            }
            return pts;
        }

        void Fractal(Random r, float[] mags, int start, int end, float ampl, float ampd)
        {
            float mag = (end - start) / (float)mags.Length * ampl;
            int mid = (start + end) / 2;
            float at = (mags[start] + mags[end]) / 2 + mag * (float)r.NextDouble() - mag / 2;
            mags[mid] = at + ampd * (float)r.NextDouble() - ampd / 2;
            if ((start + end) % 2 == 1) mags[end - 1] = mags[end];
            if (mid - start > 1) Fractal(r, mags, start, mid, ampl, ampd);
            if (end - mid > 1) Fractal(r, mags, mid, end, ampl, ampd);
        }

        // the up of a look from one point to another (Z up)
        static Vector3 LookUp(Vector3 from, Vector3 to)
        {
            Vector3 f = (to - from).Normalized();
            Vector3 r = Vector3.Cross(f, Vector3.UnitZ);
            if (r.LengthSquared < 1e-8f) r = Vector3.UnitX;
            return Vector3.Cross(r.Normalized(), f).Normalized();
        }

        // each live bolt's first and last point (effect space), keyed by the bolt: Retro runs a GPSM and an EPSM system
        // per bolt there, emitting while it lives (CParticleElectric: x420_calculatedVerts front / back). Added for the
        // Shooting Gallery turret's slot arcs (CampaignRenderer.TurretFx.cs), 2026-10-16
        public void Ends(List<(object Bolt, Vector3 Start, Vector3 End)> into)
        {
            foreach (Bolt b in _live)
            {
                if (b.Points.Length > 0) into.Add((b, b.Points[0], b.Points[^1]));
            }
        }

        // the lines to draw this frame, core first
        public void Collect(List<Line> into)
        {
            foreach (Bolt b in _live)
            {
                var pts = new Vector3[b.Points.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = Vector3.TransformPosition(b.Points[i], Transform);
                for (int k = 1; k <= 3; k++)
                {
                    float w = b.Width[k - 1];
                    if (w <= 0 || _d["LWD" + k] == null) continue;
                    Vector4 col = Color(_d["LCL" + k], b.Age, b.Life);
                    if ((int)(Math.Clamp(col.W, 0, 1) * 255) <= 0) continue;   // the game's alpha test: alpha > 0
                    into.Add(new Line(pts, w, col, k, Texture is 0 or UInt32.MaxValue ? 0 : Texture));
                }
            }
        }

        // ---- the elements an ELSC uses ----

        float Rnd() => (float)_rand.NextDouble();

        static float Range(Random r, float a, float b) => a + (b - a) * (float)r.NextDouble();

        Vector3 RandomDir()
        {
            while (true)
            {
                var v = new Vector3(Rnd() * 2 - 1, Rnd() * 2 - 1, Rnd() * 2 - 1);
                float l = v.LengthSquared;
                if (l > 1e-4f && l <= 1) return v / MathF.Sqrt(l);
            }
        }

        // ASPH (origin, X bias, Y bias, X range, Y range, radius, speed): a direction X/Y degrees off +Z, each its bias
        // plus a random share of its range; the point radius out along it, the velocity speed along it (Retro takes the
        // direction from the point, which a radius of 0, as Corruption's Hyper Ball has, leaves without one: the angles'
        // own direction here). SPHE (origin, radius, speed): any direction. SEMR: a point and a velocity.
        (Vector3 Pos, Vector3 Vel) Emit(Part.Elem? e, Vector3 none)
        {
            if (e == null) return (none, Vector3.Zero);
            int f = _frame;
            switch (e.Kind)
            {
                case "SEMR": return (Vec(e.Kid(0), f), Vec(e.Kid(1), f));
                case "SPHE":
                {
                    Vector3 dir = RandomDir();
                    return (Vec(e.Kid(0), f) + dir * Real(e.Kid(1), 0, f, 1), dir * Real(e.Kid(2), 0, f, 1));
                }
                case "ASPH":
                {
                    Vector3 origin = Vec(e.Kid(0), f);
                    float xb = Real(e.Kid(1), 0, f, 1), yb = Real(e.Kid(2), 0, f, 1);
                    float xr = Real(e.Kid(3), 0, f, 1), yr = Real(e.Kid(4), 0, f, 1);
                    float radius = Real(e.Kid(5), 0, f, 1), speed = Real(e.Kid(6), 0, f, 1);
                    float x = MathHelper.DegreesToRadians(xb + xr * 0.5f - Rnd() * xr);
                    float y = MathHelper.DegreesToRadians(yb + yr * 0.5f - Rnd() * yr);
                    var dir = new Vector3(-MathF.Sin(y) * MathF.Cos(x), MathF.Sin(x), MathF.Cos(x) * MathF.Cos(y));
                    return (origin + dir * radius, dir * speed);
                }
            }
            return (Vec(e, f), Vector3.Zero);
        }

        int Int(Part.Elem? e, int dflt)
        {
            if (e == null) return dflt;
            switch (e.Kind)
            {
                case "CNST": return e.I;
                case "RAND":
                {
                    int a = Int(e.Kid(0), 0), b = Int(e.Kid(1), 0);
                    return a + (int)(Rnd() * (b - a + 1));
                }
                case "ADD_": return Int(e.Kid(0), 0) + Int(e.Kid(1), 0);
                case "MULT": return Int(e.Kid(0), 0) * Int(e.Kid(1), 0);
                case "KEYE": case "KEYP": return (int)Keyed(e, 0, 1, 1, 0);
            }
            return dflt;
        }

        float Real(Part.Elem? e, float dflt, int frame, int life)
        {
            if (e == null) return dflt;
            switch (e.Kind)
            {
                case "CNST": return e.F;
                case "RAND": case "IRND": return MathHelper.Lerp(Real(e.Kid(0), 0, frame, life), Real(e.Kid(1), 0, frame, life), Rnd());
                case "ADD_": return Real(e.Kid(0), 0, frame, life) + Real(e.Kid(1), 0, frame, life);
                case "SUB_": return Real(e.Kid(0), 0, frame, life) - Real(e.Kid(1), 0, frame, life);
                case "MULT": return Real(e.Kid(0), 0, frame, life) * Real(e.Kid(1), 0, frame, life);
                case "KEYE": case "KEYP": return Keyed(e, frame, life, 1, 0);
                // PULS(a, b, A, B): Retro's CREPulse, A while frame % (a + b + 1) < a, else B (b < 1: always A)
                case "PULS": return Real(Pulse(e, frame) ? e.Kid(2) : e.Kid(3), 0, frame, life);
            }
            return dflt;
        }

        // CREPulse / CVEPulse's choice (PrimeDecomp CRealElement.cpp / CVectorElement.cpp): true = A. The real one switches
        // to B once frame % (a + b + 1) >= a, the vector one once it is > a
        bool Pulse(Part.Elem e, int frame, bool vector = false)
        {
            int a = Int(e.Kid(0), 0), b = Int(e.Kid(1), 0);
            int cv = a + b + 1;
            if (cv < 0) cv = 1;
            if (b < 1 || cv == 0) return true;
            int at = frame % cv;
            return vector ? at <= a : at < a;
        }

        Vector3 Vec(Part.Elem? e, int frame = 0)
        {
            if (e == null) return Vector3.Zero;
            switch (e.Kind)
            {
                case "CNST": return new Vector3(Real(e.Kid(0), 0, frame, 1), Real(e.Kid(1), 0, frame, 1), Real(e.Kid(2), 0, frame, 1));
                case "RNDV": return RandomDir() * Real(e.Kid(0), 0, frame, 1);
                case "ADD_": return Vec(e.Kid(0), frame) + Vec(e.Kid(1), frame);
                case "PULS": return Vec(Pulse(e, frame, vector: true) ? e.Kid(2) : e.Kid(3), frame);
                case "KEYE": case "KEYP": return new Vector3(Keyed(e, 0, 1, 3, 0), Keyed(e, 0, 1, 3, 1), Keyed(e, 0, 1, 3, 2));
            }
            return Vector3.Zero;
        }

        Vector4 Color(Part.Elem? e, int frame, int life)
        {
            if (e == null) return Vector4.One;
            switch (e.Kind)
            {
                case "CNST":
                    return new Vector4(Real(e.Kid(0), 0, frame, life), Real(e.Kid(1), 0, frame, life), Real(e.Kid(2), 0, frame, life),
                        Real(e.Kid(3), 0, frame, life));
                case "KEYE": case "KEYP":
                    return new Vector4(Keyed(e, frame, life, 4, 0), Keyed(e, frame, life, 4, 1), Keyed(e, frame, life, 4, 2), Keyed(e, frame, life, 4, 3));
                case "CFDL":
                    return Vector4.Lerp(Color(e.Kid(0), frame, life), Color(e.Kid(1), frame, life), Math.Clamp(frame / (float)Math.Max(1, life), 0, 1));
                case "MULT": return Color(e.Kid(0), frame, life) * Color(e.Kid(1), frame, life);
                case "ADD_": return Color(e.Kid(0), frame, life) + Color(e.Kid(1), frame, life);
            }
            return Vector4.One;
        }

        // KEYP: 100 keys by lifetime percent, lerped; KEYE: a key per frame, looping LoopStart..LoopEnd
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
                return MathHelper.Lerp(k[i * width + lane], k[(i + 1) * width + lane], pct - i);
            }
            int f = frame;
            if (e.Loop && e.LoopEnd > e.LoopStart && f >= e.LoopEnd) f = (f - e.LoopStart) % (e.LoopEnd - e.LoopStart) + e.LoopStart;
            else if (e.Loop && n > 0) f %= n;
            f = Math.Clamp(f, 0, n - 1);
            return k[f * width + lane];
        }
    }
}
