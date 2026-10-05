using System;
using System.IO;

namespace MphRecomp.Import.Retro
{
    // LZO1X decompression: a C# port of lzokay's decompress (https://github.com/jackoalan/lzokay, MIT License,
    // Copyright (c) 2018 Jack Andersen; AxioDL's LZO library, the one PrimeWorldEditor uses). lzokay was written from
    // the stream format in the Linux sources (Documentation/lzo.txt): one instruction byte at a time, M1..M4 back
    // references, and a state = how many literals the previous instruction copied. Echoes stores its PAK resources as
    // size-prefixed segments of this; Prime 3's CMPD blocks too.
    // Ship-ready S12 (2026-10-04): replaces our first decoder, whose control flow followed the GPL-2 reference
    // lzo1x_decompress; the two gave identical bytes on every LZO segment of Echoes and of Prime 3.
    public static class Lzo1x
    {
        // dstLen: the most this stream may produce (Echoes passes what's left of the resource); returns what it produced
        public static byte[] Decompress(byte[] src, int dstLen)
        {
            if (src.Length < 3)
            {
                throw new InvalidDataException("LZO: input too short");
            }
            var dst = new byte[dstLen];
            int ip = 0, op = 0, state = 0, lblen = 0;
            // first byte: 22..255 = a literal run of (byte - 17), state 4; 18..21 = (byte - 17) literals, state 1..4;
            // 0..17 = an ordinary instruction
            if (src[0] >= 22)
            {
                CopyLiterals(src, ref ip, dst, ref op, src[ip++] - 17);
                state = 4;
            }
            else if (src[0] >= 18)
            {
                state = src[ip++] - 17;
                CopyLiterals(src, ref ip, dst, ref op, state);
            }
            while (true)
            {
                NeedIn(src, ip, 1);
                int inst = src[ip++];
                int back, nstate;
                if ((inst & 0xC0) != 0)
                {
                    // M2: 0 1 L D D D S S (64..127) or 1 L L D D D S S (128..255), then one byte H:
                    // length (inst >> 5) + 1 = 3..8, distance (H << 3) + D + 1, then S literals
                    NeedIn(src, ip, 1);
                    back = (src[ip++] << 3) + ((inst >> 2) & 7) + 1;
                    lblen = (inst >> 5) + 1;
                    nstate = inst & 3;
                }
                else if ((inst & 0x20) != 0)
                {
                    // M3: 0 0 1 L L L L L (32..63), then LE16 D D D D D D D D : D D D D D D S S:
                    // length 2 + (L, or 31 + the zero-run extension), distance D + 1
                    lblen = (inst & 0x1F) + 2;
                    if (lblen == 2)
                    {
                        lblen += ZeroRunLength(src, ref ip) + 31;
                    }
                    NeedIn(src, ip, 2);
                    nstate = src[ip] | (src[ip + 1] << 8);
                    ip += 2;
                    back = (nstate >> 2) + 1;
                    nstate &= 3;
                }
                else if ((inst & 0x10) != 0)
                {
                    // M4: 0 0 0 1 H L L L (16..31), then LE16 as M3: length 2 + (L, or 7 + the zero-run extension),
                    // distance 16384 + (H << 14) + D; a distance of exactly 16384 ends the stream
                    lblen = (inst & 7) + 2;
                    if (lblen == 2)
                    {
                        lblen += ZeroRunLength(src, ref ip) + 7;
                    }
                    NeedIn(src, ip, 2);
                    nstate = src[ip] | (src[ip + 1] << 8);
                    ip += 2;
                    back = ((inst & 8) << 11) + (nstate >> 2);
                    nstate &= 3;
                    if (back == 0)
                    {
                        break;
                    }
                    back += 16384;
                }
                else if (state == 0)
                {
                    // 0 0 0 0 L L L L (0..15) after an instruction that copied no literals: a literal run of
                    // 3 + (L, or 15 + the zero-run extension), state 4
                    int len = inst + 3;
                    if (len == 3)
                    {
                        len += ZeroRunLength(src, ref ip) + 15;
                    }
                    CopyLiterals(src, ref ip, dst, ref op, len);
                    state = 4;
                    continue;
                }
                else if (state != 4)
                {
                    // M1 after 1..3 literals: 0 0 0 0 D D S S, then one byte H: 2 bytes from distance (H << 2) + D + 1
                    NeedIn(src, ip, 1);
                    nstate = inst & 3;
                    back = (inst >> 2) + (src[ip++] << 2) + 1;
                    lblen = 2;
                }
                else
                {
                    // M1 after 4 or more literals: as above, 3 bytes from distance (H << 2) + D + 2049
                    NeedIn(src, ip, 1);
                    nstate = inst & 3;
                    back = (inst >> 2) + (src[ip++] << 2) + 2049;
                    lblen = 3;
                }
                if (back > op)
                {
                    throw new InvalidDataException("LZO: back reference before the start of the output");
                }
                NeedIn(src, ip, nstate);
                NeedOut(dst, op, lblen + nstate);
                // byte by byte: a back reference may overlap what it writes
                for (int i = 0; i < lblen; i++, op++)
                {
                    dst[op] = dst[op - back];
                }
                state = nstate;
                for (int i = 0; i < nstate; i++)
                {
                    dst[op++] = src[ip++];
                }
            }
            if (lblen != 3)
            {
                throw new InvalidDataException("LZO: the stream doesn't end with the end-of-stream marker");
            }
            if (op == dst.Length)
            {
                return dst;
            }
            var res = new byte[op];
            Buffer.BlockCopy(dst, 0, res, 0, op);
            return res;
        }

        // a length's extension: 255 for every zero byte, plus the first non-zero byte
        static int ZeroRunLength(byte[] src, ref int ip)
        {
            int start = ip;
            while (ip < src.Length && src[ip] == 0)
            {
                ip++;
            }
            NeedIn(src, ip, 1);
            return (ip - start) * 255 + src[ip++];
        }

        static void CopyLiterals(byte[] src, ref int ip, byte[] dst, ref int op, int n)
        {
            NeedIn(src, ip, n);
            NeedOut(dst, op, n);
            Buffer.BlockCopy(src, ip, dst, op, n);
            ip += n;
            op += n;
        }

        static void NeedIn(byte[] src, int ip, int n)
        {
            if (ip + n > src.Length)
            {
                throw new InvalidDataException("LZO: the input ends early");
            }
        }

        static void NeedOut(byte[] dst, int op, int n)
        {
            if (op + n > dst.Length)
            {
                throw new InvalidDataException("LZO: more output than expected");
            }
        }
    }
}
