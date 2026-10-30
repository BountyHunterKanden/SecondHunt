using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace MphRecomp.Online
{
    // Vanilla-style friend codes: 12 digits shown 1234-5678-9012. 11 digits come from a hash of the player's public key,
    // the 12th is a Luhn check digit, so a mistyped digit is caught on the keypad before anything is sent. The server
    // binds a code to the first key that claims it (docs/ONLINE_PLAY.md section 4).
    public static class FriendCodes
    {
        public const int Length = 12;

        public static string FromKey(ReadOnlySpan<byte> publicKey)
        {
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(publicKey)) % 100_000_000_000UL;
            string body = v.ToString("D11");
            return body + CheckDigit(body);
        }

        public static string Format(string code) => code.Length == Length ? $"{code[..4]}-{code[4..8]}-{code[8..]}" : code;

        // digits from anything typed or pasted ("1234 5678 9012", "1234-5678-9012"); null unless 12 digits with a good check
        public static string? Parse(string? text)
        {
            if (text == null)
            {
                return null;
            }
            if (text.Any(c => !Char.IsAsciiDigit(c) && c != '-' && c != ' '))
            {
                return null;
            }
            string digits = new string(text.Where(Char.IsAsciiDigit).ToArray());
            return IsValid(digits) ? digits : null;
        }

        public static bool IsValid(string? code)
            => code != null && code.Length == Length && code.All(Char.IsAsciiDigit) && CheckDigit(code[..^1]) == code[^1];

        private static char CheckDigit(string body)
        {
            int sum = 0;
            for (int i = body.Length - 1, p = 0; i >= 0; i--, p++)
            {
                int d = body[i] - '0';
                if (p % 2 == 0)
                {
                    d *= 2;
                    if (d > 9)
                    {
                        d -= 9;
                    }
                }
                sum += d;
            }
            return (char)('0' + (10 - sum % 10) % 10);
        }
    }

    // The backup / transfer code: the identity's 16-byte seed in Crockford base32 (no I L O U; typing I/L as 1 and O as 0
    // is accepted), plus 2 check characters, as 7 groups of 4: XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX. Whoever has it IS the
    // player, so it's shown only in the player's own settings.
    public static class BackupCodes
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const int SeedChars = 26; // 130 bits for a 128-bit seed (the top 2 are zero)
        private const int Chars = SeedChars + 2;

        public static string Encode(ReadOnlySpan<byte> seed)
        {
            if (seed.Length != OnlineIdentity.SeedBytes)
            {
                throw new ArgumentException("seed size", nameof(seed));
            }
            var v = new BigInteger(seed, isUnsigned: true, isBigEndian: true);
            char[] chars = new char[Chars];
            for (int i = SeedChars - 1; i >= 0; i--)
            {
                chars[i] = Alphabet[(int)(v % 32)];
                v /= 32;
            }
            int check = Check(seed);
            chars[SeedChars] = Alphabet[check >> 5];
            chars[SeedChars + 1] = Alphabet[check & 31];
            var sb = new StringBuilder();
            for (int i = 0; i < Chars; i++)
            {
                if (i > 0 && i % 4 == 0)
                {
                    sb.Append('-');
                }
                sb.Append(chars[i]);
            }
            return sb.ToString();
        }

        public static bool TryDecode(string? text, out byte[] seed)
        {
            seed = Array.Empty<byte>();
            if (text == null)
            {
                return false;
            }
            var digits = new StringBuilder();
            foreach (char raw in text.ToUpperInvariant())
            {
                char c = raw switch { 'I' or 'L' => '1', 'O' => '0', _ => raw };
                if (c == '-' || c == ' ')
                {
                    continue;
                }
                if (Alphabet.IndexOf(c) < 0)
                {
                    return false;
                }
                digits.Append(c);
            }
            if (digits.Length != Chars)
            {
                return false;
            }
            BigInteger v = 0;
            for (int i = 0; i < SeedChars; i++)
            {
                v = v * 32 + Alphabet.IndexOf(digits[i]);
            }
            if (v >> 128 != 0)
            {
                return false;
            }
            byte[] bytes = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            byte[] padded = new byte[OnlineIdentity.SeedBytes];
            bytes.CopyTo(padded, padded.Length - bytes.Length);
            int check = Alphabet.IndexOf(digits[SeedChars]) << 5 | Alphabet.IndexOf(digits[SeedChars + 1]);
            if (check != Check(padded))
            {
                return false;
            }
            seed = padded;
            return true;
        }

        private static int Check(ReadOnlySpan<byte> seed)
        {
            byte[] h = SHA256.HashData(seed);
            return (h[0] << 2 | h[1] >> 6) & 0x3FF;
        }
    }
}
