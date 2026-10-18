using System;
using System.IO;

namespace MphRecomp.PrimeMusic
{
    // Prime 1's four Audio/*.rsf files (frontend_1, frontend_2, samusjak, ending3: the title, menu and ending music):
    // headerless CCITT G.721 (32 kbit/s ADPCM, 4-bit codes), stereo at 32,000 Hz, the first half of the file the left
    // channel and the second half the right, each byte two samples, low nibble first. Nothing in the file says whether it
    // loops: the recipe does. Found by the 2026-10-14 survey on GM8E01; Trilogy's copies are the same format.
    public sealed class RsfMusic : IMusicStream
    {
        public const int Rate = 32000;

        readonly byte[] _file;
        readonly int _half;

        public RsfMusic(byte[] file, bool loops)
        {
            if (file.Length < 4) throw new InvalidDataException("RSF file too short");
            _file = file;
            _half = file.Length / 2;
            SampleCount = _half * 2;
            Loops = loops;
            LoopEnd = SampleCount;
        }

        public int Channels => 2;
        public int SampleRate => Rate;
        public int SampleCount { get; }
        public bool Loops { get; }
        public int LoopStart => 0;
        public int LoopEnd { get; }

        public IMusicReader OpenReader() => new Reader(this);

        sealed class Reader : IMusicReader
        {
            readonly RsfMusic _m;
            G721 _left = new(), _right = new();
            long _position;
            bool _finished;

            public Reader(RsfMusic m) { _m = m; }

            public long Position => _position;
            public bool Finished => _finished;

            public void Restart()
            {
                _position = 0;
                _finished = false;
                _left = new G721();
                _right = new G721();
            }

            public int Read(Span<short> destination, int frames)
            {
                if (destination.Length < (long)frames * 2) throw new ArgumentException("destination too small", nameof(destination));
                int written = 0;
                while (written < frames)
                {
                    if (_position >= _m.SampleCount)
                    {
                        if (!_m.Loops)
                        {
                            _finished = true;
                            break;
                        }
                        // a G.721 decoder adapts as it goes: a loop starts it afresh, as the start of the file did
                        Restart();
                    }
                    int i = (int)(_position >> 1), shift = (int)(_position & 1) * 4;
                    destination[written * 2] = _left.Decode(_m._file[i] >> shift);
                    destination[written * 2 + 1] = _right.Decode(_m._file[_m._half + i] >> shift);
                    written++;
                    _position++;
                }
                if (!_m.Loops && _position >= _m.SampleCount) _finished = true;
                return written;
            }
        }
    }

    // A CCITT G.721 decoder (4-bit codes to 16-bit linear PCM), after Sun Microsystems' reference implementation
    // (g72x.c / g721.c, "provided for unrestricted use"; THIRD_PARTY_NOTICES.md): the adaptive quantizer, its scale factor
    // with the fast/slow speed control, and the 2-pole / 6-zero adaptive predictor, every value kept in the reference's
    // integer widths.
    public sealed class G721
    {
        static readonly short[] Power2 = { 1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80, 0x100, 0x200, 0x400, 0x800, 0x1000, 0x2000, 0x4000 };
        static readonly short[] DqlnTab = { -2048, 4, 135, 213, 273, 323, 373, 425, 425, 373, 323, 273, 213, 135, 4, -2048 };
        static readonly short[] WiTab = { -12, 18, 41, 64, 112, 198, 355, 1122, 1122, 355, 198, 112, 64, 41, 18, -12 };
        static readonly short[] FiTab = { 0, 0, 0, 0x200, 0x200, 0x200, 0x600, 0xE00, 0xE00, 0x600, 0x200, 0x200, 0x200, 0, 0, 0 };

        int _yl = 34816;        // the slow scale factor (long in the reference)
        short _yu = 544;        // the fast one
        short _dms, _dml;       // short- and long-term averages of the code's magnitude
        short _ap;              // the speed control
        readonly short[] _a = new short[2], _b = new short[6];  // pole and zero coefficients
        readonly short[] _pk = new short[2];                     // signs of the last partial reconstructions
        readonly short[] _dq = { 32, 32, 32, 32, 32, 32 };       // the last 6 quantized differences (4-bit exp, 6-bit mantissa)
        readonly short[] _sr = { 32, 32 };                       // the last 2 reconstructed signals, the same way
        bool _td;               // a tone was detected

        // how many of Power2's entries val reaches
        static int Quan(int val)
        {
            int i = 0;
            while (i < Power2.Length && val >= Power2[i]) i++;
            return i;
        }

        // an (a predictor coefficient) times srn (a floating-point signal)
        static int FMult(int an, int srn)
        {
            int anmag = an > 0 ? an : (-an) & 0x1FFF;
            int anexp = Quan(anmag) - 6;
            int anmant = anmag == 0 ? 32 : anexp >= 0 ? anmag >> anexp : anmag << -anexp;
            int wanexp = anexp + ((srn >> 6) & 0xF) - 13;
            int wanmant = (anmant * (srn & 0x3F) + 0x30) >> 4;
            int retval = wanexp >= 0 ? (wanmant << wanexp) & 0x7FFF : wanmant >> -wanexp;
            return (an ^ srn) < 0 ? -retval : retval;
        }

        int StepSize()
        {
            if (_ap >= 256) return _yu;
            int y = _yl >> 6;
            int dif = _yu - y;
            int al = _ap >> 2;
            if (dif > 0) y += (dif * al) >> 6;
            else if (dif < 0) y += (dif * al + 0x3F) >> 6;
            return y;
        }

        static int Reconstruct(bool sign, int dqln, int y)
        {
            int dql = (short)(dqln + (y >> 2));
            if (dql < 0) return sign ? -0x8000 : 0;
            int dex = (dql >> 7) & 15;
            int dqt = 128 + (dql & 127);
            int dq = (dqt << 7) >> (14 - dex);
            return sign ? dq - 0x8000 : dq;
        }

        // one 4-bit code to a 16-bit sample
        public short Decode(int code)
        {
            code &= 15;
            int sezi = FMult(_b[0] >> 2, _dq[0]);
            for (int i = 1; i < 6; i++) sezi += FMult(_b[i] >> 2, _dq[i]);
            sezi = (short)sezi;
            int sez = sezi >> 1;
            int sei = (short)(sezi + FMult(_a[1] >> 2, _sr[1]) + FMult(_a[0] >> 2, _sr[0]));
            int se = sei >> 1;
            int y = StepSize();
            int dq = (short)Reconstruct((code & 8) != 0, DqlnTab[code], y);
            int sr = (short)(dq < 0 ? se - (dq & 0x3FFF) : se + dq);
            int dqsez = (short)(sr - se + sez);
            Update(y, WiTab[code] << 5, FiTab[code], dq, sr, dqsez);
            return (short)Math.Clamp(sr << 2, short.MinValue, short.MaxValue);
        }

        static short Float(int mag, bool negative)
        {
            int exp = Quan(mag);
            int v = (exp << 6) + ((mag << 6) >> exp);
            return (short)(negative ? v - 0x400 : v);
        }

        void Update(int y, int wi, int fi, int dq, int sr, int dqsez)
        {
            short pk0 = (short)(dqsez < 0 ? 1 : 0);
            int mag = dq & 0x7FFF;

            // TRANS: a transition from a tone
            int ylint = _yl >> 15;
            int ylfrac = (_yl >> 10) & 0x1F;
            int thr1 = (32 + ylfrac) << ylint;
            int thr2 = ylint > 9 ? 31 << 10 : thr1;
            int dqthr = (thr2 + (thr2 >> 1)) >> 1;
            bool tr = _td && mag > dqthr;

            // the quantizer scale factor
            int yu = y + ((wi - y) >> 5);
            _yu = (short)Math.Clamp(yu, 544, 5120);
            _yl += _yu + ((-_yl) >> 6);

            // the predictor coefficients
            int a2p = 0;
            if (tr)
            {
                Array.Clear(_a);
                Array.Clear(_b);
            }
            else
            {
                int pks1 = pk0 ^ _pk[0];
                a2p = _a[1] - (_a[1] >> 7);
                if (dqsez != 0)
                {
                    int fa1 = pks1 != 0 ? _a[0] : -_a[0];
                    if (fa1 < -8191) a2p -= 0x100;
                    else if (fa1 > 8191) a2p += 0xFF;
                    else a2p += fa1 >> 5;
                    if ((pk0 ^ _pk[1]) != 0)
                    {
                        if (a2p <= -12160) a2p = -12288;
                        else if (a2p >= 12416) a2p = 12288;
                        else a2p -= 0x80;
                    }
                    else if (a2p <= -12416) a2p = -12288;
                    else if (a2p >= 12160) a2p = 12288;
                    else a2p += 0x80;
                }
                a2p = (short)a2p;
                _a[1] = (short)a2p;

                int a0 = _a[0] - (_a[0] >> 8);
                if (dqsez != 0) a0 += pks1 == 0 ? 192 : -192;
                int a1ul = 15360 - a2p;
                _a[0] = (short)Math.Clamp(a0, -a1ul, a1ul);

                for (int i = 0; i < 6; i++)
                {
                    int b = _b[i] - (_b[i] >> 8);
                    if ((dq & 0x7FFF) != 0) b += (dq ^ _dq[i]) >= 0 ? 128 : -128;
                    _b[i] = (short)b;
                }
            }

            for (int i = 5; i > 0; i--) _dq[i] = _dq[i - 1];
            _dq[0] = mag == 0 ? (short)(dq >= 0 ? 0x20 : unchecked((short)0xFC20)) : Float(mag, dq < 0);

            _sr[1] = _sr[0];
            _sr[0] = sr == 0 ? (short)0x20
                : sr > 0 ? Float(sr, false)
                : sr > -32768 ? Float(-sr, true)
                : unchecked((short)0xFC20);

            _pk[1] = _pk[0];
            _pk[0] = pk0;

            // TONE
            _td = !tr && a2p < -11776;

            // the speed control
            _dms += (short)((fi - _dms) >> 5);
            _dml += (short)(((fi << 2) - _dml) >> 7);
            if (tr) _ap = 256;
            else if (y < 1536 || _td || Math.Abs((_dms << 2) - _dml) >= (_dml >> 3)) _ap += (short)((0x200 - _ap) >> 4);
            else _ap += (short)((-_ap) >> 4);
        }
    }
}
