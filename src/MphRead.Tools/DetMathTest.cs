using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Mathematics;

namespace MphRead
{
    // Replay mode R2b: checks MphRead/Utility/DetMath.cs (the same-bits-everywhere float math).
    //   -detmathtest [n=200000]
    // For each function over its ranges and the special values: how often DetMath's float result equals MathF's (this
    // platform's C library) and the correctly rounded one (double Math, rounded), and the largest difference in units
    // in the last place; the OpenTK helper copies against OpenTK, bit for bit (with MathF's trig); speed against MathF.
    // FAIL if a special value differs, anything is more than 1 ulp from the correctly rounded result, or a copy differs.
    internal static class DetMathTest
    {
        public static void Run(string[] args)
        {
            int n = 200000;
            foreach (string arg in args[1..])
            {
                if (arg.StartsWith("n="))
                {
                    n = Int32.Parse(arg[2..]);
                }
            }
            _n = n;
            var rng = new Random(12345);
            int failures = 0;
            float R(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
            // log-uniform magnitude, random sign
            float L(float lo, float hi, bool signed) => (signed && rng.Next(2) == 0 ? -1 : 1)
                * MathF.Exp(R(MathF.Log(lo), MathF.Log(hi)));
            float[] special = { 0f, -0f, 1f, -1f, 0.5f, -0.5f, 2f, MathF.PI, -MathF.PI, MathF.PI / 2, -MathF.PI / 2,
                MathF.PI / 4, 2 * MathF.PI, 1e-30f, -1e-30f, 1e-45f, Single.MaxValue, Single.Epsilon, Single.NaN,
                Single.PositiveInfinity, Single.NegativeInfinity, 100f, 1000f, 1e6f, 0.999999f, -0.999999f, 30f };

            Console.WriteLine($"{"function",-14} {"=MathF",8} {"=correct",9} {"max ulp",8}  (vs MathF / vs correctly rounded; {n} inputs per range)");
            failures += Unary("sin", x => (float)DetMath.Sin(x), MathF.Sin, x => Math.Sin(x), special,
                () => R(-10, 10), () => R(-1000, 1000), () => L(1e-8f, 1e5f, true), () => L(1e5f, 3e38f, true));
            failures += Unary("cos", x => (float)DetMath.Cos(x), MathF.Cos, x => Math.Cos(x), special,
                () => R(-10, 10), () => R(-1000, 1000), () => L(1e-8f, 1e5f, true), () => L(1e5f, 3e38f, true));
            failures += Unary("tan", x => (float)DetMath.Tan(x), MathF.Tan, x => Math.Tan(x), special,
                () => R(-1.5f, 1.5f), () => R(-100, 100), () => L(1e5f, 3e38f, true));
            failures += Unary("atan", x => (float)DetMath.Atan(x), MathF.Atan, x => Math.Atan(x), special,
                () => R(-2, 2), () => L(1e-8f, 1e8f, true));
            failures += Unary("asin", x => (float)DetMath.Asin(x), MathF.Asin, x => Math.Asin(x), special,
                () => R(-1, 1), () => 1 - L(1e-7f, 1e-1f, false));
            failures += Unary("acos", x => (float)DetMath.Acos(x), MathF.Acos, x => Math.Acos(x), special,
                () => R(-1, 1), () => 1 - L(1e-7f, 1e-1f, false), () => -1 + L(1e-7f, 1e-1f, false));
            failures += Unary("exp", x => (float)DetMath.Exp(x), MathF.Exp, x => Math.Exp(x), special,
                () => R(-20, 20), () => R(-104, 89));
            failures += Unary("log", x => (float)DetMath.Log(x), MathF.Log, x => Math.Log(x), special,
                () => R(0, 2), () => L(1e-38f, 1e38f, false));
            failures += Binary("atan2", (y, x) => (float)DetMath.Atan2(y, x), MathF.Atan2, (y, x) => Math.Atan2(y, x),
                special, () => (R(-10, 10), R(-10, 10)), () => (L(1e-6f, 1e6f, true), L(1e-6f, 1e6f, true)));
            failures += Binary("pow", (x, y) => (float)DetMath.Pow(x, y), MathF.Pow, (x, y) => Math.Pow(x, y),
                special, () => (R(0, 10), R(-30, 30)), () => (R(0.9f, 1f), 30f), () => (R(0.5f, 1f), R(0, 1)),
                () => (MathF.Round(R(-5, 5)), MathF.Round(R(-10, 10))), () => (R(-5, 5), R(-3, 3)));

            failures += Copies(rng);
            Speed();
            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures} check(s)");
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        // ulps between two floats (same sign assumed by ordering the bits); NaN == NaN counts as equal
        private static long Ulps(float a, float b)
        {
            if (Single.IsNaN(a) || Single.IsNaN(b))
            {
                return Single.IsNaN(a) && Single.IsNaN(b) ? 0 : Int64.MaxValue;
            }
            static long Ordered(float f)
            {
                int i = BitConverter.SingleToInt32Bits(f);
                return i < 0 ? Int32.MinValue - (long)i : i;
            }
            return Math.Abs(Ordered(a) - Ordered(b));
        }

        private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)
            || (Single.IsNaN(a) && Single.IsNaN(b));

        private static int Unary(string name, Func<float, float> det, Func<float, float> mathf, Func<double, double> exact,
            float[] special, params Func<float>[] ranges)
        {
            int failures = 0;
            foreach (float x in special)
            {
                float d = det(x), c = (float)exact(x);
                if (!Same(d, c) && Ulps(d, c) > 1)
                {
                    Console.WriteLine($"  {name}({x:R}) = {d:R}, correctly rounded {c:R}, MathF {mathf(x):R}");
                    failures++;
                }
            }
            for (int r = 0; r < ranges.Length; r++)
            {
                int sameMathF = 0, sameExact = 0, n = 0;
                long maxUlp = 0;
                float worst = 0;
                for (int i = 0; i < _n; i++)
                {
                    float x = ranges[r]();
                    float d = det(x), m = mathf(x), c = (float)exact(x);
                    n++;
                    sameMathF += Same(d, m) ? 1 : 0;
                    sameExact += Same(d, c) ? 1 : 0;
                    long u = Ulps(d, c);
                    if (u > maxUlp)
                    {
                        maxUlp = u;
                        worst = x;
                    }
                }
                Console.WriteLine($"{name + " #" + (r + 1),-14} {100.0 * sameMathF / n,7:0.000}% {100.0 * sameExact / n,8:0.000}% "
                    + $"{maxUlp,8}{(maxUlp > 0 ? $"  (worst at {worst:R})" : "")}");
                failures += maxUlp > 1 ? 1 : 0;
            }
            return failures;
        }

        private static int Binary(string name, Func<float, float, float> det, Func<float, float, float> mathf,
            Func<double, double, double> exact, float[] special, params Func<(float, float)>[] ranges)
        {
            int failures = 0;
            foreach (float a in special)
            {
                foreach (float b in special)
                {
                    float d = det(a, b), c = (float)exact(a, b);
                    if (!Same(d, c) && Ulps(d, c) > 1)
                    {
                        Console.WriteLine($"  {name}({a:R}, {b:R}) = {d:R}, correctly rounded {c:R}, MathF {mathf(a, b):R}");
                        failures++;
                    }
                }
            }
            for (int r = 0; r < ranges.Length; r++)
            {
                int sameMathF = 0, sameExact = 0, n = 0;
                long maxUlp = 0;
                (float, float) worst = default;
                for (int i = 0; i < _n; i++)
                {
                    (float a, float b) = ranges[r]();
                    float d = det(a, b), m = mathf(a, b), c = (float)exact(a, b);
                    n++;
                    sameMathF += Same(d, m) ? 1 : 0;
                    sameExact += Same(d, c) ? 1 : 0;
                    long u = Ulps(d, c);
                    if (u > maxUlp)
                    {
                        maxUlp = u;
                        worst = (a, b);
                    }
                }
                Console.WriteLine($"{name + " #" + (r + 1),-14} {100.0 * sameMathF / n,7:0.000}% {100.0 * sameExact / n,8:0.000}% "
                    + $"{maxUlp,8}{(maxUlp > 0 ? $"  (worst at {worst.Item1:R}, {worst.Item2:R})" : "")}");
                failures += maxUlp > 1 ? 1 : 0;
            }
            return failures;
        }

        private static int _n = 200000;

        // DetMatrix's copies of OpenTK's helpers, fed MathF's trig, must equal OpenTK's bit for bit
        private static int Copies(Random rng)
        {
            int bad = 0;
            float R(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
            static bool Eq4(Matrix4 a, Matrix4 b)
            {
                for (int i = 0; i < 4; i++)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        if (BitConverter.SingleToInt32Bits(a[i, j]) != BitConverter.SingleToInt32Bits(b[i, j]))
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
            static bool Eq3(Matrix3 a, Matrix3 b) => Eq4(new Matrix4(a), new Matrix4(b));
            static bool EqV(Vector3 a, Vector3 b) => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
                && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
                && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
            bool was = DetMathF.HostDeterministic;
            DetMathF.HostDeterministic = false; // the copies' own trig (EulerAngles) = MathF here
            var counts = new Dictionary<string, int>();
            void Check(string what, bool ok)
            {
                if (!ok && counts.TryAdd(what, 1))
                {
                    bad++;
                }
            }
            for (int i = 0; i < 100000; i++)
            {
                float a = i < 1000 ? R(-7, 7) : R(-1000, 1000);
                float s = MathF.Sin(a), c = MathF.Cos(a), sn = MathF.Sin(-a), cn = MathF.Cos(-a);
                var axis = new Vector3(R(-2, 2), R(-2, 2), R(-2, 2));
                Check("RotationX", Eq4(DetMatrix.RotationX(s, c), Matrix4.CreateRotationX(a)));
                Check("RotationY", Eq4(DetMatrix.RotationY(s, c), Matrix4.CreateRotationY(a)));
                Check("RotationZ", Eq4(DetMatrix.RotationZ(s, c), Matrix4.CreateRotationZ(a)));
                Check("RotationY3", Eq3(DetMatrix.RotationY3(s, c), Matrix3.CreateRotationY(a)));
                Check("FromAxisAngle", Eq4(DetMatrix.FromAxisAngle(axis, sn, cn), Matrix4.CreateFromAxisAngle(axis, a)));
                Check("FromAxisAngle3", Eq3(new Matrix3(DetMatrix.FromAxisAngle(axis, sn, cn)), Matrix3.CreateFromAxisAngle(axis, a)));
                Quaternion q = Quaternion.FromAxisAngle(axis, a);
                if (i % 3 == 0)
                {
                    q = new Quaternion(R(-1, 1), R(-1, 1), R(-1, 1), R(-1, 1));
                }
                Check("EulerAngles", EqV(DetMatrix.EulerAngles(q), q.ToEulerAngles()));
                Check("off = OpenTK", Eq4(DetMatrix.CreateRotationY(a), Matrix4.CreateRotationY(a))
                    && Eq4(DetMatrix.CreateFromAxisAngle(axis, a), Matrix4.CreateFromAxisAngle(axis, a))
                    && EqV(DetMatrix.ToEulerAngles(q), q.ToEulerAngles()));
            }
            DetMathF.HostDeterministic = was;
            Console.WriteLine(bad == 0 ? "OpenTK helper copies: bit-identical to OpenTK (100000 inputs each)"
                : $"OpenTK helper copies DIFFER: {String.Join(", ", counts.Keys)}");
            return bad;
        }

        private static void Speed()
        {
            const int count = 5_000_000;
            float[] xs = new float[1024];
            var rng = new Random(7);
            for (int i = 0; i < xs.Length; i++)
            {
                xs[i] = (float)(rng.NextDouble() * 20 - 10);
            }
            foreach ((string name, Func<float, float> mathf, Func<float, float> det) in new (string, Func<float, float>, Func<float, float>)[]
            {
                ("sin", MathF.Sin, x => (float)DetMath.Sin(x)),
                ("atan2", x => MathF.Atan2(x, 1.5f), x => (float)DetMath.Atan2(x, 1.5f)),
                ("acos", x => MathF.Acos(x * 0.1f), x => (float)DetMath.Acos(x * 0.1f)),
                ("pow", x => MathF.Pow(0.9f, x), x => (float)DetMath.Pow(0.9f, x))
            })
            {
                double Time(Func<float, float> f)
                {
                    float sum = 0;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < count; i++)
                    {
                        sum += f(xs[i & 1023]);
                    }
                    GC.KeepAlive(sum);
                    return sw.Elapsed.TotalMilliseconds * 1e6 / count;
                }
                Time(mathf);
                Time(det);
                Console.WriteLine($"speed {name,-6}: MathF {Time(mathf):0.0} ns, DetMath {Time(det):0.0} ns per call");
            }
        }
    }
}
