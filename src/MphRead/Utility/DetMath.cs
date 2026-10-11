// Replay mode R2b (docs/REPLAY_MODE.md): float math that gives the same bits on every device. MathF's sin/cos/atan/...
// come from each platform's C library (Windows' CRT, Android's bionic) and differ in the last bits, so a replay recorded
// on one device drifts on another from the first turn. These are built from + - * / and square roots only (IEEE 754
// exact on every CPU and runtime), evaluated in double and rounded to float once: within an ulp of MathF's results, so
// play feels the same.
//
// Host-guarded: off by default (MphRead's own behaviour, unchanged); the recomp's match host turns it on
// (MphRecomp.Core CampaignHost.StartMatch). The alias below sends every `MathF.` in this project here; the members that
// are IEEE-exact already pass straight through. OpenTK's rotation helpers call System.MathF inside OpenTK, so their call
// sites go through DetMatrix instead.
global using MathF = MphRead.DetMathF;

using System;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;

namespace MphRead
{
    public static class DetMathF
    {
        public const float PI = System.MathF.PI;
        public const float E = System.MathF.E;
        public const float Tau = System.MathF.Tau;

        // on: the transcendental functions below (and DetMatrix) use DetMath; off: System.MathF (MphRead's own)
        public static bool HostDeterministic { get; set; }

        // IEEE 754 exact (or exactly specified in managed code): the same on every platform
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Abs(float x) => System.MathF.Abs(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Sqrt(float x) => System.MathF.Sqrt(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Min(float x, float y) => System.MathF.Min(x, y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Max(float x, float y) => System.MathF.Max(x, y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Floor(float x) => System.MathF.Floor(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Ceiling(float x) => System.MathF.Ceiling(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Truncate(float x) => System.MathF.Truncate(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static int Sign(float x) => System.MathF.Sign(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Round(float x) => System.MathF.Round(x);
        public static float Round(float x, int digits) => System.MathF.Round(x, digits);
        public static float Round(float x, int digits, MidpointRounding mode) => System.MathF.Round(x, digits, mode);
        public static float Round(float x, MidpointRounding mode) => System.MathF.Round(x, mode);

        public static float Sin(float x) => HostDeterministic ? (float)DetMath.Sin(x) : System.MathF.Sin(x);
        public static float Cos(float x) => HostDeterministic ? (float)DetMath.Cos(x) : System.MathF.Cos(x);

        public static (float Sin, float Cos) SinCos(float x)
        {
            if (!HostDeterministic)
            {
                return System.MathF.SinCos(x);
            }
            DetMath.SinCos(x, out double sin, out double cos);
            return ((float)sin, (float)cos);
        }

        public static float Tan(float x) => HostDeterministic ? (float)DetMath.Tan(x) : System.MathF.Tan(x);
        public static float Atan(float x) => HostDeterministic ? (float)DetMath.Atan(x) : System.MathF.Atan(x);
        public static float Atan2(float y, float x) => HostDeterministic ? (float)DetMath.Atan2(y, x) : System.MathF.Atan2(y, x);
        public static float Asin(float x) => HostDeterministic ? (float)DetMath.Asin(x) : System.MathF.Asin(x);
        public static float Acos(float x) => HostDeterministic ? (float)DetMath.Acos(x) : System.MathF.Acos(x);
        public static float Exp(float x) => HostDeterministic ? (float)DetMath.Exp(x) : System.MathF.Exp(x);
        public static float Log(float x) => HostDeterministic ? (float)DetMath.Log(x) : System.MathF.Log(x);
        public static float Pow(float x, float y) => HostDeterministic ? (float)DetMath.Pow(x, y) : System.MathF.Pow(x, y);
    }

    // The double kernels. Accuracy: a few units in the last place of a double, far below a float's rounding. Only + - * /,
    // sqrt, floor, comparisons and bit operations, so the result is the same bits everywhere (no FMA: .NET never fuses
    // a * b + c on its own).
    public static class DetMath
    {
        // pi/2 in three parts (fdlibm's Cody-Waite split): k * PiO2A is exact for |k| < 2^22, k * PiO2B for |k| < 2^20
        private static readonly double PiO2A = BitConverter.Int64BitsToDouble(0x3FF921FB54400000); // 1.5707963267341256
        private static readonly double PiO2B = BitConverter.Int64BitsToDouble(0x3DD0B4611A600000); // 6.077100506303966e-11
        private static readonly double PiO2C = BitConverter.Int64BitsToDouble(0x3BA3198A2E037073); // 2.0222662487959506e-21
        private const double TwoOverPi = 0.63661977236758134308;
        public const double PiO2 = 1.57079632679489661923;
        public const double Pi = 3.14159265358979323846;
        // ln 2 in two parts: k * Ln2A is exact for |k| < 2^11
        private static readonly double Ln2A = BitConverter.Int64BitsToDouble(0x3FE62E42FEE00000); // 0.6931471803691238
        private static readonly double Ln2B = BitConverter.Int64BitsToDouble(0x3DEA39EF35793C76); // 1.9082149292705877e-10
        private const double InvLn2 = 1.44269504088896338700;
        private const double Sqrt2 = 1.41421356237309504880;

        // ---- sin, cos, tan ----

        // x - k pi/2 in [-pi/4, pi/4], and k mod 4 (the quadrant)
        private static double Reduce(double x, out int quadrant)
        {
            if (Math.Abs(x) >= BigArgument)
            {
                return ReduceBig(x, out quadrant);
            }
            double k = Math.Floor(x * TwoOverPi + 0.5);
            quadrant = (int)(k - 4 * Math.Floor(k * 0.25));
            return ((x - k * PiO2A) - k * PiO2B) - k * PiO2C;
        }

        // from here on the three-part pi/2 isn't exact enough: reduce exactly instead, in integers (x = m 2^e times
        // floor(2/pi 2^TwoOverPiBits)). Slow, but no game angle gets near it.
        private const double BigArgument = 1e5;
        private const int TwoOverPiBits = 1200; // enough for any double: 2^1024 x 2/pi keeps 128 fraction bits

        private static double ReduceBig(double x, out int quadrant)
        {
            long bits = BitConverter.DoubleToInt64Bits(Math.Abs(x));
            long mantissa = (bits & 0x000FFFFFFFFFFFFF) | 0x0010000000000000; // |x| >= 1e5: normal
            int e = (int)((bits >> 52) & 0x7FF) - 1075;
            System.Numerics.BigInteger n = mantissa * BigTwoOverPi.Value; // |x| 2/pi = n / 2^shift
            int shift = TwoOverPiBits - e;
            int q = (int)(n >> shift & 3);
            System.Numerics.BigInteger half = System.Numerics.BigInteger.One << 127;
            System.Numerics.BigInteger frac = (n >> (shift - 128)) & ((half << 1) - 1); // 128 fraction bits
            if (frac >= half)
            {
                frac -= half << 1; // to [-1/2, 1/2): the next quadrant
                q++;
            }
            double f = (double)frac * Pow2(-128);
            if (x < 0)
            {
                f = -f;
                q = -q;
            }
            quadrant = q & 3;
            return f * PiO2;
        }

        // floor(2/pi 2^TwoOverPiBits), from Machin's formula in integers (computed once, on first use)
        private static class BigTwoOverPi
        {
            public static readonly System.Numerics.BigInteger Value = Compute();

            private static System.Numerics.BigInteger Compute()
            {
                int guard = TwoOverPiBits + 64;
                System.Numerics.BigInteger pi = 16 * ArctanInverse(5, guard) - 4 * ArctanInverse(239, guard); // pi 2^guard
                return (System.Numerics.BigInteger.One << (TwoOverPiBits + 1 + guard)) / pi;
            }

            // atan(1/n) 2^bits
            private static System.Numerics.BigInteger ArctanInverse(int n, int bits)
            {
                System.Numerics.BigInteger term = (System.Numerics.BigInteger.One << bits) / n;
                System.Numerics.BigInteger sum = term;
                int n2 = n * n;
                for (int k = 1; !term.IsZero; k++)
                {
                    term /= n2;
                    System.Numerics.BigInteger t = term / (2 * k + 1);
                    sum += (k & 1) != 0 ? -t : t;
                }
                return sum;
            }
        }

        // Taylor series on |r| <= pi/4 (the next terms are below 1e-19 there)
        private static double SinKernel(double r)
        {
            double z = r * r;
            double p = -1.0 / 121645100408832000;
            p = 1.0 / 355687428096000 + z * p;
            p = -1.0 / 1307674368000 + z * p;
            p = 1.0 / 6227020800 + z * p;
            p = -1.0 / 39916800 + z * p;
            p = 1.0 / 362880 + z * p;
            p = -1.0 / 5040 + z * p;
            p = 1.0 / 120 + z * p;
            p = -1.0 / 6 + z * p;
            return r + r * z * p;
        }

        private static double CosKernel(double r)
        {
            double z = r * r;
            double p = 1.0 / 2432902008176640000;
            p = -1.0 / 6402373705728000 + z * p;
            p = 1.0 / 20922789888000 + z * p;
            p = -1.0 / 87178291200 + z * p;
            p = 1.0 / 479001600 + z * p;
            p = -1.0 / 3628800 + z * p;
            p = 1.0 / 40320 + z * p;
            p = -1.0 / 720 + z * p;
            p = 1.0 / 24 + z * p;
            return 1 - z * 0.5 + z * z * p;
        }

        public static double Sin(double x)
        {
            if (!Double.IsFinite(x))
            {
                return Double.NaN;
            }
            if (x == 0)
            {
                return x; // keeps -0
            }
            double r = Reduce(x, out int q);
            return q switch
            {
                0 => SinKernel(r),
                1 => CosKernel(r),
                2 => -SinKernel(r),
                _ => -CosKernel(r)
            };
        }

        public static double Cos(double x)
        {
            if (!Double.IsFinite(x))
            {
                return Double.NaN;
            }
            double r = Reduce(x, out int q);
            return q switch
            {
                0 => CosKernel(r),
                1 => -SinKernel(r),
                2 => -CosKernel(r),
                _ => SinKernel(r)
            };
        }

        public static void SinCos(double x, out double sin, out double cos)
        {
            sin = Sin(x);
            cos = Cos(x);
        }

        public static double Tan(double x)
        {
            if (!Double.IsFinite(x))
            {
                return Double.NaN;
            }
            if (x == 0)
            {
                return x;
            }
            double r = Reduce(x, out int q);
            double s = SinKernel(r), c = CosKernel(r);
            return (q & 1) == 0 ? s / c : -c / s;
        }

        // ---- atan, atan2, asin, acos ----

        public static double Atan(double x)
        {
            if (Double.IsNaN(x))
            {
                return Double.NaN;
            }
            double a = Math.Abs(x);
            bool invert = a > 1;
            if (invert)
            {
                a = 1 / a; // +inf -> 0 -> pi/2
            }
            // atan(a) = 2 atan(a / (1 + sqrt(1 + a^2))), twice: |t| <= tan(pi/16) ~ 0.199
            double t = a / (1 + Math.Sqrt(1 + a * a));
            t /= 1 + Math.Sqrt(1 + t * t);
            // Taylor series to t^25 (the next term is below 1e-19 of the result)
            double z = t * t;
            double p = -1.0 / 25;
            p = 1.0 / 23 + z * p;
            p = -1.0 / 21 + z * p;
            p = 1.0 / 19 + z * p;
            p = -1.0 / 17 + z * p;
            p = 1.0 / 15 + z * p;
            p = -1.0 / 13 + z * p;
            p = 1.0 / 11 + z * p;
            p = -1.0 / 9 + z * p;
            p = 1.0 / 7 + z * p;
            p = -1.0 / 5 + z * p;
            p = 1.0 / 3 + z * p;
            double r = 4 * (t - t * z * p);
            if (invert)
            {
                r = PiO2 - r;
            }
            return Math.CopySign(r, x);
        }

        // C99/IEEE special cases, as MathF.Atan2: atan2(+-0, +0) = +-0, atan2(+-0, -0) = +-pi, etc.
        public static double Atan2(double y, double x)
        {
            if (Double.IsNaN(x) || Double.IsNaN(y))
            {
                return Double.NaN;
            }
            if (y == 0)
            {
                return Double.IsNegative(x) ? Math.CopySign(Pi, y) : y;
            }
            if (x == 0)
            {
                return Math.CopySign(PiO2, y);
            }
            if (Double.IsInfinity(x))
            {
                if (Double.IsInfinity(y))
                {
                    return Math.CopySign(x > 0 ? PiO2 / 2 : 3 * PiO2 / 2, y);
                }
                return x > 0 ? Math.CopySign(0.0, y) : Math.CopySign(Pi, y);
            }
            if (Double.IsInfinity(y))
            {
                return Math.CopySign(PiO2, y);
            }
            double t = Atan(Math.Abs(y) / Math.Abs(x));
            if (x < 0)
            {
                t = Pi - t;
            }
            return Math.CopySign(t, y);
        }

        public static double Asin(double x)
        {
            if (!(Math.Abs(x) <= 1))
            {
                return Double.NaN;
            }
            return Atan2(x, Math.Sqrt((1 - x) * (1 + x)));
        }

        public static double Acos(double x)
        {
            if (!(Math.Abs(x) <= 1))
            {
                return Double.NaN;
            }
            return Atan2(Math.Sqrt((1 - x) * (1 + x)), x);
        }

        // ---- exp, log, pow ----

        // 2^k as a double, exactly (subnormal and overflowing k included)
        private static double Pow2(int k)
        {
            if (k > 1023)
            {
                return k > 2046 ? Double.PositiveInfinity : Pow2(1023) * Pow2(k - 1023);
            }
            if (k < -1022)
            {
                return k < -1022 - 54 - 1022 ? 0 : Pow2(k + 54) * Pow2(-54);
            }
            return BitConverter.Int64BitsToDouble((long)(k + 1023) << 52);
        }

        public static double Exp(double x)
        {
            if (Double.IsNaN(x))
            {
                return Double.NaN;
            }
            if (x > 709.8)
            {
                return Double.PositiveInfinity;
            }
            if (x < -745.2)
            {
                return 0;
            }
            double k = Math.Floor(x * InvLn2 + 0.5);
            double r = (x - k * Ln2A) - k * Ln2B; // |r| <= ln2 / 2
            // Taylor series to r^15
            double p = 1.0 / 1307674368000;
            p = 1.0 / 87178291200 + r * p;
            p = 1.0 / 6227020800 + r * p;
            p = 1.0 / 479001600 + r * p;
            p = 1.0 / 39916800 + r * p;
            p = 1.0 / 3628800 + r * p;
            p = 1.0 / 362880 + r * p;
            p = 1.0 / 40320 + r * p;
            p = 1.0 / 5040 + r * p;
            p = 1.0 / 720 + r * p;
            p = 1.0 / 120 + r * p;
            p = 1.0 / 24 + r * p;
            p = 1.0 / 6 + r * p;
            p = 0.5 + r * p;
            p = 1 + r * (1 + r * p);
            return p * Pow2((int)k);
        }

        public static double Log(double x)
        {
            if (Double.IsNaN(x) || x < 0)
            {
                return Double.NaN;
            }
            if (x == 0)
            {
                return Double.NegativeInfinity;
            }
            if (Double.IsPositiveInfinity(x))
            {
                return x;
            }
            int e = 0;
            if (x < 2.2250738585072014e-308) // subnormal: scale into the normal range first
            {
                x *= Pow2(54);
                e = -54;
            }
            long bits = BitConverter.DoubleToInt64Bits(x);
            e += (int)((bits >> 52) & 0x7FF) - 1023;
            double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFF) | 0x3FF0000000000000); // [1, 2)
            if (m > Sqrt2)
            {
                m *= 0.5;
                e++;
            }
            // log m = 2 atanh(s), s = (m - 1) / (m + 1), |s| <= 0.1716; series to s^25
            double s = (m - 1) / (m + 1);
            double z = s * s;
            double p = 1.0 / 25;
            p = 1.0 / 23 + z * p;
            p = 1.0 / 21 + z * p;
            p = 1.0 / 19 + z * p;
            p = 1.0 / 17 + z * p;
            p = 1.0 / 15 + z * p;
            p = 1.0 / 13 + z * p;
            p = 1.0 / 11 + z * p;
            p = 1.0 / 9 + z * p;
            p = 1.0 / 7 + z * p;
            p = 1.0 / 5 + z * p;
            p = 1.0 / 3 + z * p;
            double logm = 2 * (s + s * z * p);
            return e * Ln2A + (e * Ln2B + logm);
        }

        private static bool IsInteger(double y) => Math.Floor(y) == y;

        private static bool IsOddInteger(double y) => IsInteger(y) && Math.Abs(y) < 9007199254740992.0 && ((long)y & 1) != 0;

        // C99/IEEE special cases, as MathF.Pow
        public static double Pow(double x, double y)
        {
            if (y == 0 || x == 1)
            {
                return 1;
            }
            if (Double.IsNaN(x) || Double.IsNaN(y))
            {
                return Double.NaN;
            }
            if (Double.IsInfinity(y))
            {
                double a = Math.Abs(x);
                if (a == 1)
                {
                    return 1;
                }
                return (a < 1) == (y < 0) ? Double.PositiveInfinity : 0;
            }
            if (x == 0)
            {
                bool odd = IsOddInteger(y);
                if (y < 0)
                {
                    return odd ? Math.CopySign(Double.PositiveInfinity, x) : Double.PositiveInfinity;
                }
                return odd ? x : 0;
            }
            if (Double.IsInfinity(x))
            {
                bool odd = IsOddInteger(y);
                if (x > 0)
                {
                    return y < 0 ? 0 : Double.PositiveInfinity;
                }
                if (y < 0)
                {
                    return odd ? -0.0 : 0;
                }
                return odd ? Double.NegativeInfinity : Double.PositiveInfinity;
            }
            double sign = 1;
            if (x < 0)
            {
                if (!IsInteger(y))
                {
                    return Double.NaN;
                }
                if (IsOddInteger(y))
                {
                    sign = -1;
                }
                x = -x;
            }
            return sign * Exp(y * Log(x));
        }
    }

    // OpenTK's rotation helpers (OpenTK.Mathematics 4.9.4, Matrix4.cs / Matrix3.cs / Quaternion.cs) with their own
    // arithmetic, step for step, and the trig from DetMathF. Off: OpenTK's own.
    public static class DetMatrix
    {
        public static Matrix4 CreateRotationX(float angle) => DetMathF.HostDeterministic
            ? RotationX(DetMathF.Sin(angle), DetMathF.Cos(angle)) : Matrix4.CreateRotationX(angle);

        public static Matrix4 CreateRotationY(float angle) => DetMathF.HostDeterministic
            ? RotationY(DetMathF.Sin(angle), DetMathF.Cos(angle)) : Matrix4.CreateRotationY(angle);

        public static Matrix4 CreateRotationZ(float angle) => DetMathF.HostDeterministic
            ? RotationZ(DetMathF.Sin(angle), DetMathF.Cos(angle)) : Matrix4.CreateRotationZ(angle);

        public static Matrix4 CreateFromAxisAngle(Vector3 axis, float angle) => DetMathF.HostDeterministic
            ? FromAxisAngle(axis, DetMathF.Sin(-angle), DetMathF.Cos(-angle)) : Matrix4.CreateFromAxisAngle(axis, angle);

        public static Matrix3 CreateRotationY3(float angle) => DetMathF.HostDeterministic
            ? RotationY3(DetMathF.Sin(angle), DetMathF.Cos(angle)) : Matrix3.CreateRotationY(angle);

        public static Matrix3 CreateFromAxisAngle3(Vector3 axis, float angle) => DetMathF.HostDeterministic
            ? new Matrix3(FromAxisAngle(axis, DetMathF.Sin(-angle), DetMathF.Cos(-angle))) : Matrix3.CreateFromAxisAngle(axis, angle);

        public static Vector3 ToEulerAngles(Quaternion q) => DetMathF.HostDeterministic ? EulerAngles(q) : q.ToEulerAngles();

        // ---- the OpenTK bodies, given sin and cos (internal: Tools checks them bit for bit against OpenTK's) ----

        internal static Matrix4 RotationX(float sin, float cos)
        {
            Matrix4 result = Matrix4.Identity;
            result.Row1.Y = cos;
            result.Row1.Z = sin;
            result.Row2.Y = -sin;
            result.Row2.Z = cos;
            return result;
        }

        internal static Matrix4 RotationY(float sin, float cos)
        {
            Matrix4 result = Matrix4.Identity;
            result.Row0.X = cos;
            result.Row0.Z = -sin;
            result.Row2.X = sin;
            result.Row2.Z = cos;
            return result;
        }

        internal static Matrix4 RotationZ(float sin, float cos)
        {
            Matrix4 result = Matrix4.Identity;
            result.Row0.X = cos;
            result.Row0.Y = sin;
            result.Row1.X = -sin;
            result.Row1.Y = cos;
            return result;
        }

        internal static Matrix3 RotationY3(float sin, float cos)
        {
            Matrix3 result = Matrix3.Identity;
            result.Row0.X = cos;
            result.Row0.Z = -sin;
            result.Row2.X = sin;
            result.Row2.Z = cos;
            return result;
        }

        // sin and cos of -angle, as OpenTK takes them
        internal static Matrix4 FromAxisAngle(Vector3 axis, float sin, float cos)
        {
            axis.Normalize();
            float axisX = axis.X, axisY = axis.Y, axisZ = axis.Z;
            float t = 1.0f - cos;
            float tXX = t * axisX * axisX;
            float tXY = t * axisX * axisY;
            float tXZ = t * axisX * axisZ;
            float tYY = t * axisY * axisY;
            float tYZ = t * axisY * axisZ;
            float tZZ = t * axisZ * axisZ;
            float sinX = sin * axisX;
            float sinY = sin * axisY;
            float sinZ = sin * axisZ;
            Matrix4 result;
            result.Row0.X = tXX + cos;
            result.Row0.Y = tXY - sinZ;
            result.Row0.Z = tXZ + sinY;
            result.Row0.W = 0;
            result.Row1.X = tXY + sinZ;
            result.Row1.Y = tYY + cos;
            result.Row1.Z = tYZ - sinX;
            result.Row1.W = 0;
            result.Row2.X = tXZ - sinY;
            result.Row2.Y = tYZ + sinX;
            result.Row2.Z = tZZ + cos;
            result.Row2.W = 0;
            result.Row3 = Vector4.UnitW;
            return result;
        }

        internal static Vector3 EulerAngles(Quaternion q)
        {
            Vector3 eulerAngles;
            const float SINGULARITY_THRESHOLD = 0.4999995f;
            float sqw = q.W * q.W;
            float sqx = q.X * q.X;
            float sqy = q.Y * q.Y;
            float sqz = q.Z * q.Z;
            float unit = sqx + sqy + sqz + sqw;
            float singularityTest = (q.X * q.Z) + (q.W * q.Y);
            if (singularityTest > SINGULARITY_THRESHOLD * unit)
            {
                eulerAngles.Z = 2 * DetMathF.Atan2(q.X, q.W);
                eulerAngles.Y = MathHelper.PiOver2;
                eulerAngles.X = 0;
            }
            else if (singularityTest < -SINGULARITY_THRESHOLD * unit)
            {
                eulerAngles.Z = -2 * DetMathF.Atan2(q.X, q.W);
                eulerAngles.Y = -MathHelper.PiOver2;
                eulerAngles.X = 0;
            }
            else
            {
                eulerAngles.Z = DetMathF.Atan2(2 * ((q.W * q.Z) - (q.X * q.Y)), sqw + sqx - sqy - sqz);
                eulerAngles.Y = DetMathF.Asin(2 * singularityTest / unit);
                eulerAngles.X = DetMathF.Atan2(2 * ((q.W * q.X) - (q.Y * q.Z)), sqw - sqx - sqy + sqz);
            }
            return eulerAngles;
        }
    }
}
