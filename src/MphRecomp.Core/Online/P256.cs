using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace MphRecomp.Online
{
    // NIST P-256 point multiplication in plain managed code. Used only to turn a backup seed's private scalar into its
    // public point, so the platform crypto can import both (importing a private scalar alone isn't supported everywhere).
    // Not constant-time: it runs once per start, on the player's own device. Checked against the platform's own key
    // generation by `SecondHunt.Server --selftest`.
    internal static class P256
    {
        private static readonly BigInteger P = Hex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
        public static readonly BigInteger N = Hex("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
        private static readonly BigInteger Gx = Hex("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296");
        private static readonly BigInteger Gy = Hex("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5");

        private static BigInteger Hex(string hex) => BigInteger.Parse("0" + hex, NumberStyles.HexNumber);

        private static BigInteger Mod(BigInteger x)
        {
            x %= P;
            return x.Sign < 0 ? x + P : x;
        }

        private static BigInteger Inverse(BigInteger x) => BigInteger.ModPow(Mod(x), P - 2, P);

        // the private scalar for a seed: SHA-256 of a fixed context + the seed + a counter, the first value in [1, n)
        public static BigInteger ScalarFromSeed(ReadOnlySpan<byte> seed)
        {
            byte[] context = "secondhunt-key-v1"u8.ToArray();
            byte[] input = new byte[context.Length + seed.Length + 1];
            context.CopyTo(input, 0);
            seed.CopyTo(input.AsSpan(context.Length));
            for (int counter = 0; counter < 256; counter++)
            {
                input[^1] = (byte)counter;
                var d = new BigInteger(SHA256.HashData(input), isUnsigned: true, isBigEndian: true);
                if (d.Sign > 0 && d < N)
                {
                    return d;
                }
            }
            throw new CryptographicException("no scalar for this seed");
        }

        // k * G as two 32-byte big-endian coordinates
        public static (byte[] X, byte[] Y) MultiplyBase(BigInteger k)
        {
            if (k.Sign <= 0 || k >= N)
            {
                throw new ArgumentOutOfRangeException(nameof(k));
            }
            bool infinity = true;
            BigInteger rx = 0, ry = 0;
            BigInteger qx = Gx, qy = Gy;
            while (k.Sign > 0)
            {
                if (!k.IsEven)
                {
                    if (infinity)
                    {
                        (rx, ry, infinity) = (qx, qy, false);
                    }
                    else
                    {
                        (rx, ry, infinity) = Add(rx, ry, qx, qy);
                    }
                }
                (qx, qy) = Double(qx, qy);
                k >>= 1;
            }
            return (Bytes(rx), Bytes(ry));
        }

        private static (BigInteger, BigInteger, bool) Add(BigInteger x1, BigInteger y1, BigInteger x2, BigInteger y2)
        {
            if (x1 == x2)
            {
                if (y1 == y2 && y1.Sign != 0)
                {
                    (BigInteger dx, BigInteger dy) = Double(x1, y1);
                    return (dx, dy, false);
                }
                return (0, 0, true);
            }
            BigInteger l = Mod((y2 - y1) * Inverse(x2 - x1));
            BigInteger x3 = Mod(l * l - x1 - x2);
            return (x3, Mod(l * (x1 - x3) - y1), false);
        }

        private static (BigInteger, BigInteger) Double(BigInteger x, BigInteger y)
        {
            // a = -3
            BigInteger l = Mod((3 * x * x - 3) * Inverse(2 * y));
            BigInteger x3 = Mod(l * l - 2 * x);
            return (x3, Mod(l * (x - x3) - y));
        }

        public static byte[] Bytes(BigInteger v)
        {
            byte[] raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            byte[] padded = new byte[32];
            raw.CopyTo(padded, 32 - raw.Length);
            return padded;
        }
    }
}
